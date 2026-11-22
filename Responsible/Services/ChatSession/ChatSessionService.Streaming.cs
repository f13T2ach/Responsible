using System.Text.Json;

namespace Responsible.Services;

public sealed partial class ChatSessionService
{
    private void OnKernelStatus(string sessionId, bool running)
    {
        if (sessionId != CurrentSessionId) return;
        RunningChanged?.Invoke(running);
        if (running) _ = RefreshPermissionIfMissingAsync(sessionId);
    }

    private void OnKernelEvent(string sessionId, JsonElement ev)
    {
        if (sessionId != CurrentSessionId) return;
        var type = ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("type", out var t) ? t.GetString() : null;
        string? id;
        lock (_gate) id = _kernelAssistantId;
        if (id is null) return;

        switch (type)
        {
            case "assistant/message":
                HandleAssistantMessage(id, ev);
                break;
            case "turn/end":
                var failure = TurnEndFailure(ev);
                string? errorChunk = null;
                ChatMessage? settled = null;
                lock (_gate)
                {
                    if (_kernelAssistantId != id) return;
                    _kernelAssistantId = null;
                    _lastTurnAssistantId = id;
                    CommitStreamLocked();
                    var index = _messages.FindIndex(m => m.Id == id);
                    if (index >= 0)
                    {
                        long? ms = _reasoningStarted is { } started
                            ? (long)(DateTime.UtcNow - started).TotalMilliseconds
                            : null;
                        _reasoningStarted = null;
                        var text = _messages[index].Text;
                        errorChunk = failure is null
                            ? null
                            : (text.Length > 0 ? "\n\n" : "") + "Request failed: " + failure;
                        // 结果帧丢失/轮次中断时，未落定的工具按已停止收尾，不假装成功
                        var tools = _messages[index].Tools;
                        if (tools is { } pending && pending.Any(tool => tool.State == "running"))
                            tools = pending
                                .Select(tool => tool.State == "running" ? tool with { State = "stopped" } : tool)
                                .ToArray();
                        _messages[index] = _messages[index] with
                        {
                            Text = text + errorChunk,
                            State = "done",
                            ReasoningMs = ms ?? _messages[index].ReasoningMs,
                            Tools = tools,
                        };
                        settled = _messages[index];
                    }
                }
                if (settled is not null && settled.Tools is { Count: > 0 }) MessageUpdated?.Invoke(settled);
                if (errorChunk is not null) Delivered?.Invoke(id, errorChunk);
                RunningChanged?.Invoke(false);
                Completed?.Invoke(id, "done");
                _ = FinishChangesAsync();
                _ = RefreshConversationsAsync();
                break;
        }
    }

    // 工具调用帧：call 先落一条运行中条目，result 用同一 callId 收尾；条目挂在本轮回复气泡上
    private void OnKernelTool(string sessionId, DshToolCallFrame frame)
    {
        if (sessionId != CurrentSessionId) return;
        ChatMessage? updated = null;
        lock (_gate)
        {
            var targetId = _kernelAssistantId;
            if (targetId is null && frame.Phase == "result")
            {
                var owner = _messages.FindLast(message => message.Role == "assistant"
                    && message.Tools is { } tools && tools.Any(tool => tool.CallId == frame.CallId));
                targetId = owner?.Id;
            }
            if (targetId is null) return;
            var index = _messages.FindIndex(message => message.Id == targetId);
            if (index < 0) return;

            var message = _messages[index];
            var tools = message.Tools is { } existing ? existing.ToList() : [];
            if (frame.Phase == "call")
            {
                if (tools.Any(tool => tool.CallId == frame.CallId)) return;
                tools.Add(new ChatToolCall(
                    frame.CallId,
                    frame.Name,
                    frame.Variant,
                    frame.Summary,
                    "running",
                    null,
                    frame.Input.Length > 0 ? frame.Input : null,
                    frame.Ts));
            }
            else
            {
                var at = tools.FindIndex(tool => tool.CallId == frame.CallId);
                if (at < 0) return;
                tools[at] = tools[at] with
                {
                    State = frame.IsError ? "error" : "ok",
                    Output = frame.Output ?? frame.Error,
                    IsError = frame.IsError,
                };
            }
            _messages[index] = message with { Tools = tools };
            updated = _messages[index];
        }
        if (updated is not null) MessageUpdated?.Invoke(updated);
    }

    // 插件事件管道里的 token 流帧：start 开启新 step，chunk 追加增量
    private void OnAssistantStream(string sessionId, JsonElement frame)
    {
        if (sessionId != CurrentSessionId) return;
        var type = frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("type", out var t)
            ? t.GetString()
            : null;
        switch (type)
        {
            case "start":
            {
                string id;
                string? textPrefix = null;
                string? reasoningPrefix = null;
                lock (_gate)
                {
                    var assistantId = _kernelAssistantId;
                    if (assistantId is null) return;
                    var index = _messages.FindIndex(m => m.Id == assistantId);
                    if (index < 0 || _messages[index].State != "streaming") return;
                    CommitStreamLocked();
                    _streamOpen = true;
                    // 多 step 回复之间保留空行：先让 Web 端补上分隔符，后续 delta 才能直接追加
                    if (_committedText.Length > 0)
                    {
                        textPrefix = "\n\n";
                        _stepText = textPrefix;
                    }
                    if (_committedReasoning.Length > 0)
                    {
                        reasoningPrefix = "\n\n";
                        _stepReasoning = reasoningPrefix;
                    }
                    _messages[index] = _messages[index] with
                    {
                        Text = _committedText + _stepText,
                        Reasoning = ComposeReasoningLocked() ?? _messages[index].Reasoning,
                    };
                    id = assistantId;
                }
                if (textPrefix is not null) Delivered?.Invoke(id, textPrefix);
                if (reasoningPrefix is not null) ReasoningDelivered?.Invoke(id, reasoningPrefix);
                break;
            }
            case "chunk":
                if (!frame.TryGetProperty("chunk", out var chunk) || chunk.ValueKind != JsonValueKind.Object) return;
                var kind = chunk.TryGetProperty("type", out var kt) ? kt.GetString() : null;
                if (!chunk.TryGetProperty("text", out var textElement)) return;
                var delta = textElement.GetString();
                if (string.IsNullOrEmpty(delta)) return;
                if (kind == "text-delta") AppendTextDelta(delta);
                else if (kind == "reasoning-delta") AppendReasoningDelta(delta);
                break;
        }
    }

    private void AppendTextDelta(string delta)
    {
        string id;
        lock (_gate)
        {
            if (!_streamOpen) return;
            var assistantId = _kernelAssistantId;
            if (assistantId is null) return;
            var index = _messages.FindIndex(m => m.Id == assistantId);
            if (index < 0 || _messages[index].State != "streaming") return;
            _stepText += delta;
            _messages[index] = _messages[index] with { Text = _committedText + _stepText };
            id = assistantId;
        }
        Delivered?.Invoke(id, delta);
    }

    private void AppendReasoningDelta(string delta)
    {
        string id;
        lock (_gate)
        {
            if (!_streamOpen) return;
            var assistantId = _kernelAssistantId;
            if (assistantId is null) return;
            var index = _messages.FindIndex(m => m.Id == assistantId);
            if (index < 0 || _messages[index].State != "streaming") return;
            _stepReasoning += delta;
            _reasoningStarted ??= DateTime.UtcNow;
            _messages[index] = _messages[index] with { Reasoning = ComposeReasoningLocked() };
            id = assistantId;
        }
        ReasoningDelivered?.Invoke(id, delta);
    }

    // DSH 投影统计帧：轮次/步数/tok/s/token 桶全部来自内核，宿主只做展示字段映射
    private void OnKernelStats(string sessionId, DshSessionStats stats)
    {
        if (sessionId != CurrentSessionId) return;
        lock (_gate)
        {
            _stats = ToChatStats(stats);
            PublishStatsLocked();
        }
    }

    // 插件在 turn/end 处按 token-meter 同口径算好本轮账单：挂到本轮收盘回复（最后一条 assistant）
    private void OnKernelTurn(string sessionId, DshTurnFrame frame)
    {
        if (sessionId != CurrentSessionId) return;
        ChatMessage? updated = null;
        lock (_gate)
        {
            var targetId = _kernelAssistantId ?? _lastTurnAssistantId;
            var index = targetId is null ? -1 : _messages.FindIndex(m => m.Id == targetId && m.Role == "assistant");
            // 锁定不到本轮气泡（例如历史刚重置）：不做任何归属推测
            if (index < 0) return;
            if (_messages[index].Text.Trim().Length > 0)
            {
                _messages[index] = _messages[index] with
                {
                    Usage = ToChatUsage(frame.Usage),
                    BranchSeq = frame.Seq,
                    BranchUnavailable = false,
                };
                updated = _messages[index];
            }
        }
        if (updated is not null) MessageUpdated?.Invoke(updated);
    }

    private static ChatTurnUsage ToChatUsage(DshTurnUsage usage) => new(
        usage.UncachedInputTokens,
        usage.OutputTokens,
        usage.TotalTokens,
        usage.CacheReadTokens,
        usage.CacheWriteTokens,
        usage.ReasoningTokens,
        usage.Routes.Select(route => new ChatUsageRoute(route.Provider, route.Model)).ToArray());

    private static ChatStats ToChatStats(DshSessionStats stats) => new(
        stats.Turns,
        stats.Steps,
        (int)Math.Round(stats.TokensPerSecond),
        stats.TotalTokens,
        stats.CacheHitRate * 100.0,
        stats.PressureTokens is { } pressure && stats.ContextWindow is { } window && window > 0
            ? Math.Clamp(pressure * 100.0 / window, 0, 100)
            : 0);

    // 调用方须持有 _gate：把当前流式段并入已提交段
    private void CommitStreamLocked()
    {
        _committedText += _stepText;
        _committedReasoning += _stepReasoning;
        _stepText = string.Empty;
        _stepReasoning = string.Empty;
        _streamOpen = false;
    }

    // 调用方须持有 _gate
    private string? ComposeReasoningLocked() =>
        _committedReasoning.Length == 0 && _stepReasoning.Length == 0
            ? null
            : _committedReasoning + _stepReasoning;

    private void HandleAssistantMessage(string id, JsonElement ev)
    {
        var finalText = ExtractText(ev);
        var finalReasoning = ExtractBlockText(ev, "reasoning");
        string? textChunk = null;
        string? reasoningChunk = null;
        string? restartText = null;
        string? restartReasoning = null;
        var restart = false;

        lock (_gate)
        {
            var index = _messages.FindIndex(m => m.Id == id);
            if (index < 0 || _messages[index].State != "streaming") return;

            if (_streamOpen)
            {
                // 流式段与已提交事件对账：通常只差末尾一小段；若顺序回退则整段重发。
                // 已提交事件缺少正文/思考块时保留流式结果，避免误判成回退
                var textTarget = (_committedText.Length > 0 ? "\n\n" : string.Empty) + finalText;
                if (finalText.Length > 0 && textTarget != _stepText)
                {
                    if (textTarget.StartsWith(_stepText, StringComparison.Ordinal))
                    {
                        textChunk = textTarget[_stepText.Length..];
                    }
                    else
                    {
                        restart = true;
                    }
                    _stepText = textTarget;
                }

                var reasoningTarget =
                    (_committedReasoning.Length > 0 ? "\n\n" : string.Empty) + finalReasoning;
                if (finalReasoning.Length > 0 && reasoningTarget != _stepReasoning)
                {
                    if (reasoningTarget.StartsWith(_stepReasoning, StringComparison.Ordinal))
                    {
                        reasoningChunk = reasoningTarget[_stepReasoning.Length..];
                    }
                    else
                    {
                        restart = true;
                    }
                    _stepReasoning = reasoningTarget;
                }

                CommitStreamLocked();
                _messages[index] = _messages[index] with
                {
                    Text = _committedText,
                    Reasoning = ComposeReasoningLocked() ?? _messages[index].Reasoning,
                };
                if (restart)
                {
                    restartText = _committedText;
                    restartReasoning = ComposeReasoningLocked();
                }
            }
            else if (finalText.Length > 0 || finalReasoning.Length > 0)
            {
                // 流管道不可用时退回整段追加
                if (finalText.Length > 0)
                {
                    textChunk = (_committedText.Length > 0 ? "\n\n" : string.Empty) + finalText;
                    _committedText += textChunk;
                }
                if (finalReasoning.Length > 0)
                {
                    reasoningChunk = (_committedReasoning.Length > 0 ? "\n\n" : string.Empty) + finalReasoning;
                    _committedReasoning += reasoningChunk;
                }
                _messages[index] = _messages[index] with
                {
                    Text = _committedText,
                    Reasoning = ComposeReasoningLocked() ?? _messages[index].Reasoning,
                };
            }
        }

        if (restart)
        {
            // restart 会清空该气泡的正文与思考链，必须整段重放
            Restarted?.Invoke(id);
            if (!string.IsNullOrEmpty(restartText)) Delivered?.Invoke(id, restartText);
            if (!string.IsNullOrEmpty(restartReasoning)) ReasoningDelivered?.Invoke(id, restartReasoning);
            return;
        }
        if (!string.IsNullOrEmpty(textChunk)) Delivered?.Invoke(id, textChunk);
        if (!string.IsNullOrEmpty(reasoningChunk)) ReasoningDelivered?.Invoke(id, reasoningChunk);
    }

    private async Task FinishChangesAsync()
    {
        var changes = await _changeTracker.EndAsync().ConfigureAwait(false);
        if (changes is null || changes.Total == 0) return;
        lock (_gate) _changes.Add(changes);
        ChangesReady?.Invoke(changes);
    }

    private static string? TurnEndFailure(JsonElement ev)
    {
        if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.Object) return null;
        var kind = reason.TryGetProperty("kind", out var k) ? k.GetString() : null;
        return kind switch
        {
            "error" => reason.TryGetProperty("error", out var error)
                       && error.ValueKind == JsonValueKind.Object
                       && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : "Model call failed",
            "aborted" => "Stopped",
            "blocked" => "Request blocked",
            "max-tokens" => "Output limit exceeded",
            "interrupted" => "Conversation interrupted",
            _ => null,
        };
    }

    private void FailAssistant(string id, string message)
    {
        string? chunk = null;
        lock (_gate)
        {
            var index = _messages.FindIndex(m => m.Id == id);
            if (index >= 0 && _messages[index].State == "streaming")
            {
                var prefix = _messages[index].Text.Length > 0 ? "\n\n" : "";
                chunk = prefix + "Request failed: " + message;
                _messages[index] = _messages[index] with
                {
                    Text = _messages[index].Text + chunk,
                    State = "done",
                };
            }
            _kernelAssistantId = null;
            _streamOpen = false;
            _stepText = string.Empty;
            _stepReasoning = string.Empty;
        }
        if (chunk is not null) Delivered?.Invoke(id, chunk);
        RunningChanged?.Invoke(false);
        Completed?.Invoke(id, "done");
    }

    private static string ExtractText(JsonElement ev) => ExtractBlockText(ev, "text");

    private static string ExtractBlockText(JsonElement ev, string type)
    {
        if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!data.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var bt) && bt.GetString() == type
                && block.TryGetProperty("text", out var text))
                parts.Add(text.GetString() ?? "");
        return string.Concat(parts);
    }
}
