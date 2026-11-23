using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Responsible.Services;

// DSH 进程内的 responsible-host 插件在 127.0.0.1:1107 提供只读会话/模型接口；
// 所有请求带启动时生成的 token，端口只绑定 loopback。
public sealed record DshQuestionOption(string Label, string? Description);

public sealed record DshQuestionItem(
    string Id,
    string Question,
    string? Detail,
    string? Header,
    bool MultiSelect,
    IReadOnlyList<DshQuestionOption> Options);

public sealed record DshQuestionRow(
    string Id,
    string SessionId,
    IReadOnlyList<DshQuestionItem> Questions,
    long? WaitMs);

public sealed record DshWorkspaceFile(string Name, bool Dir, long Size);

public sealed record DshApprovalRow(
    string Id,
    string ToolName,
    string? CallId,
    string? Reason,
    string SessionId,
    bool Dangerous);

internal static class DshBridgeHttpClient
{
    // 只连 127.0.0.1：必须绕开系统代理。开了代理的机器（ProxyOverride 为空时）
    // 会把本机请求也送进代理，代理对内核对端返回 502，症状就是会话列表永远加载不出来。
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<DshSessionRow>> GetSessionsAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<SessionsEnvelope>(bridge, "/sessions", cancellationToken).ConfigureAwait(false);
        return (envelope.Sessions ?? [])
            .Select(row => new DshSessionRow(
                row.SessionId ?? string.Empty,
                row.Title ?? string.Empty,
                row.Cwd,
                row.CreatedAt,
                row.UpdatedAt,
                row.Live))
            .Where(row => row.SessionId.Length > 0)
            .ToArray();
    }

    public static async Task<IReadOnlyList<DshHistoryMessage>> GetHistoryAsync(
        DshBridgeInfo bridge, string sessionId, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<HistoryEnvelope>(
            bridge, $"/sessions/{Uri.EscapeDataString(sessionId)}/history", cancellationToken).ConfigureAwait(false);
        var toolCache = new Dictionary<string, DshToolCall>(StringComparer.Ordinal);
        DshToolCall ToTool(ToolRow tool)
        {
            if (tool.CallId is { Length: > 0 } callId && toolCache.TryGetValue(callId, out var cached))
                return cached;
            var mapped = new DshToolCall(
                tool.CallId ?? string.Empty,
                tool.Name ?? string.Empty,
                tool.Variant ?? "tools",
                tool.Summary ?? string.Empty,
                tool.State ?? "ok",
                tool.Output,
                tool.Input,
                tool.Ts,
                tool.IsError);
            if (tool.CallId is { Length: > 0 }) toolCache[tool.CallId] = mapped;
            return mapped;
        }

        DshHistoryPart ToPart(PartRow part) =>
            new(part.Kind ?? string.Empty, part.Text ?? string.Empty, part.Tool is null ? null : ToTool(part.Tool));

        return (envelope.Messages ?? [])
            .Select(message => new DshHistoryMessage(
                message.Role ?? string.Empty,
                message.Text ?? string.Empty,
                message.Reasoning,
                message.Ts,
                message.Model,
                ToTurnUsage(message.Usage),
                message.BranchSeq,
                message.BranchUnavailable,
                (message.Tools ?? [])
                    .Where(tool => !string.IsNullOrEmpty(tool.CallId))
                    .Select(ToTool)
                    .ToArray(),
                (message.Parts ?? [])
                    .Where(part => part.Kind is "text" or "reasoning" or "tool")
                    .Select(ToPart)
                    .ToArray()))
            .Where(message => message.Role.Length > 0
                && (message.Text.Length > 0
                    || !string.IsNullOrEmpty(message.Reasoning)
                    || message.Parts.Count > 0
                    || message.Tools.Count > 0))
            .ToArray();
    }

    public static async Task<string?> ForkSessionAsync(
        DshBridgeInfo bridge, string sessionId, long atSeq, CancellationToken cancellationToken)
    {
        var envelope = await SendAsync<ForkEnvelope>(
            bridge, HttpMethod.Post, $"/sessions/{Uri.EscapeDataString(sessionId)}/fork",
            new { atSeq }, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(envelope.SessionId) ? null : envelope.SessionId;
    }

    private static DshTurnUsage? ToTurnUsage(UsageRow? usage)
    {
        if (usage is null
            || usage.UncachedInputTokens is not { } input
            || usage.OutputTokens is not { } output
            || usage.TotalTokens is not { } total)
            return null;
        var routes = (usage.Routes ?? [])
            .Where(route => !string.IsNullOrWhiteSpace(route.Provider) && !string.IsNullOrWhiteSpace(route.Model))
            .Select(route => new DshUsageRoute(route.Provider!, route.Model!))
            .ToArray();
        return new DshTurnUsage(
            input,
            output,
            total,
            usage.CacheReadTokens,
            usage.CacheWriteTokens,
            usage.ReasoningTokens,
            routes);
    }

    public static async Task<DshSessionStats?> GetStatsAsync(
        DshBridgeInfo bridge, string sessionId, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<StatsEnvelope>(
            bridge, $"/sessions/{Uri.EscapeDataString(sessionId)}/stats", cancellationToken).ConfigureAwait(false);
        var stats = envelope.Stats;
        return stats is null
            ? null
            : new DshSessionStats(
                stats.Turns,
                stats.Steps,
                stats.LlmMs,
                stats.ToolMs,
                stats.TtftMs,
                stats.DecodeMs,
                stats.DecodeTokens,
                stats.TokensPerSecond,
                stats.InputTokens,
                stats.OutputTokens,
                stats.CacheReadTokens,
                stats.CacheWriteTokens,
                stats.TotalTokens,
                stats.CacheHitRate,
                stats.PressureTokens,
                stats.ContextWindow);
    }

    public static async Task<IReadOnlyList<DshModelRow>> GetModelsAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<ModelsEnvelope>(bridge, "/models", cancellationToken).ConfigureAwait(false);
        return (envelope.Models ?? [])
            .Select(model => new DshModelRow(
                model.ProviderId ?? string.Empty,
                string.IsNullOrWhiteSpace(model.ProviderName) ? model.ProviderId ?? string.Empty : model.ProviderName!,
                model.ModelId ?? string.Empty,
                string.IsNullOrWhiteSpace(model.ModelName) ? model.ModelId ?? string.Empty : model.ModelName!))
            .Where(model => model.ProviderId.Length > 0 && model.ModelId.Length > 0)
            .ToArray();
    }

    public static async Task<bool> SetCredentialAsync(
        DshBridgeInfo bridge, string reference, string value, CancellationToken cancellationToken)
    {
        var envelope = await SendAsync<SetCredentialEnvelope>(
            bridge, HttpMethod.Put, "/credentials", new { @ref = reference, value }, cancellationToken)
            .ConfigureAwait(false);
        return envelope.Stored;
    }

    public static async Task<DshPermissionCatalog?> GetPermissionCatalogAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<PermissionCatalogEnvelope>(bridge, "/permissions", cancellationToken)
            .ConfigureAwait(false);
        var catalog = envelope.Catalog;
        if (catalog?.Options is null) return null;
        var options = catalog.Options
            .Where(option => !string.IsNullOrWhiteSpace(option.Value))
            .Select(option => new DshPermissionOption(
                option.Value!,
                string.IsNullOrWhiteSpace(option.Name) ? option.Value! : option.Name!,
                option.Description))
            .ToArray();
        return new DshPermissionCatalog(options, catalog.DefaultPreset ?? string.Empty);
    }

    public static async Task<string?> GetPermissionAsync(
        DshBridgeInfo bridge, string sessionId, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<PermissionSelectionEnvelope>(
            bridge, $"/permissions/{Uri.EscapeDataString(sessionId)}", cancellationToken).ConfigureAwait(false);
        return envelope.CurrentValue;
    }

    public static async Task<string?> SetPermissionAsync(
        DshBridgeInfo bridge, string sessionId, string preset, CancellationToken cancellationToken)
    {
        var envelope = await SendAsync<PermissionSelectionEnvelope>(
            bridge, HttpMethod.Post, $"/permissions/{Uri.EscapeDataString(sessionId)}",
            new { preset }, cancellationToken).ConfigureAwait(false);
        return envelope.CurrentValue;
    }

    public static async Task<IReadOnlyList<DshQuestionRow>> ReadQuestionsAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<QuestionsEnvelope>(bridge, "/questions", cancellationToken)
            .ConfigureAwait(false);
        return envelope.Questions ?? [];
    }

    public static async Task<bool> AnswerQuestionAsync(
        DshBridgeInfo bridge, string id, object answers, CancellationToken cancellationToken)
    {
        var envelope = await SendAsync<ApprovalAnswerEnvelope>(
            bridge, HttpMethod.Post, $"/questions/{Uri.EscapeDataString(id)}",
            new { answers }, cancellationToken).ConfigureAwait(false);
        return envelope.Ok;
    }

    public static async Task<IReadOnlyList<DshWorkspaceFile>> ReadFilesAsync(
        DshBridgeInfo bridge, string sessionId, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<FilesEnvelope>(
            bridge, $"/files/{Uri.EscapeDataString(sessionId)}", cancellationToken).ConfigureAwait(false);
        return envelope.Entries ?? [];
    }

    public static async Task<IReadOnlyList<DshApprovalRow>> ReadApprovalsAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<ApprovalsEnvelope>(bridge, "/approvals", cancellationToken)
            .ConfigureAwait(false);
        return envelope.Approvals ?? [];
    }

    // 答案只有 allowed-once / rejected / cancelled / unavailable 四个值，别的会被插件拒绝
    public static async Task<bool> AnswerApprovalAsync(
        DshBridgeInfo bridge, string id, string outcome, CancellationToken cancellationToken)
    {
        var envelope = await SendAsync<ApprovalAnswerEnvelope>(
            bridge, HttpMethod.Post, $"/approvals/{Uri.EscapeDataString(id)}",
            new { outcome }, cancellationToken).ConfigureAwait(false);
        return envelope.Ok;
    }

    private static Task<T> GetAsync<T>(DshBridgeInfo bridge, string path, CancellationToken cancellationToken) =>
        SendAsync<T>(bridge, HttpMethod.Get, path, null, cancellationToken);

    private static async Task<T> SendAsync<T>(
        DshBridgeInfo bridge, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{bridge.Port}{path}");
                request.Headers.TryAddWithoutValidation("x-responsible-token", bridge.Token);
                if (body is not null)
                {
                    var json = JsonSerializer.Serialize(body, JsonOptions);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new BridgeRequestException(
                        ErrorMessage(text) ?? $"Bridge endpoint returned {(int)response.StatusCode}",
                        (int)response.StatusCode);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)
                       ?? throw new IOException("The bridge endpoint returned an empty response");
            }
            catch (BridgeRequestException ex) when (ex.StatusCode is 502 or 503 or 504)
            {
                // 网关错=本机端口还没开始监听（内核仍在启动、或中间有代理代答），
                // 属于传输层暂时不可用，按可重试处理；其他状态码是真错误，直接抛。
                last = ex;
            }
            catch (BridgeRequestException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
            {
                last = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out connecting to the DSH port bridge: {last?.Message}");
    }

    private sealed class BridgeRequestException(string message, int statusCode = 0) : IOException(message)
    {
        public int StatusCode { get; } = statusCode;
    }

    private static string? ErrorMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record SessionsEnvelope(bool Ok, SessionRow[]? Sessions);

    private sealed record ApprovalsEnvelope(bool Ok, DshApprovalRow[]? Approvals);

    private sealed record FilesEnvelope(bool Ok, string? Cwd, DshWorkspaceFile[]? Entries);

    private sealed record QuestionsEnvelope(bool Ok, DshQuestionRow[]? Questions);

    private sealed record ApprovalAnswerEnvelope(bool Ok);
    private sealed record SessionRow(
        string? SessionId, string? Title, string? Cwd, long CreatedAt, long UpdatedAt, bool Live);

    private sealed record HistoryEnvelope(bool Ok, string? SessionId, string? Cwd, HistoryRow[]? Messages);
    private sealed record HistoryRow(
        string? Role,
        string? Text,
        string? Reasoning,
        long Ts,
        string? Model,
        UsageRow? Usage,
        long? BranchSeq,
        bool BranchUnavailable,
        ToolRow[]? Tools,
        PartRow[]? Parts);
    private sealed record PartRow(string? Kind, string? Text, ToolRow? Tool);
    private sealed record ToolRow(
        string? CallId,
        string? Name,
        string? Variant,
        string? Summary,
        string? State,
        string? Output,
        string? Input,
        long Ts,
        bool IsError);
    private sealed record UsageRow(
        long? UncachedInputTokens,
        long? OutputTokens,
        long? TotalTokens,
        long? CacheReadTokens,
        long? CacheWriteTokens,
        long? ReasoningTokens,
        UsageRouteRow[]? Routes);
    private sealed record UsageRouteRow(string? Provider, string? Model);

    private sealed record ForkEnvelope(bool Ok, string? SessionId, long ForkSeq);

    private sealed record ModelsEnvelope(bool Ok, ProviderRow[]? Providers, ModelRow[]? Models);
    private sealed record ProviderRow(string? Id, string? Name);
    private sealed record ModelRow(string? ProviderId, string? ProviderName, string? ModelId, string? ModelName);

    private sealed record StatsEnvelope(bool Ok, StatsRow? Stats);
    private sealed record StatsRow(
        int Turns,
        int Steps,
        long LlmMs,
        long ToolMs,
        long TtftMs,
        long DecodeMs,
        long DecodeTokens,
        double TokensPerSecond,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long TotalTokens,
        double CacheHitRate,
        long? PressureTokens,
        long? ContextWindow);

    private sealed record SetCredentialEnvelope(bool Ok, bool Stored);

    private sealed record PermissionCatalogEnvelope(bool Ok, PermissionCatalogRow? Catalog);
    private sealed record PermissionCatalogRow(PermissionOptionRow[]? Options, string? DefaultPreset);
    private sealed record PermissionOptionRow(string? Value, string? Name, string? Description);
    private sealed record PermissionSelectionEnvelope(bool Ok, string? SessionId, string? CurrentValue);
}
