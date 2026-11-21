using System.IO;
using System.Net.Http;
using System.Text.Json;
using Responsible.ViewModel;

namespace Responsible.Services;

public sealed partial class ChatSessionService
{
    private ModelOptionViewModel? CurrentOption() =>
        _catalog.ModelOptions.FirstOrDefault(o => o.IsSelected) ?? _catalog.ModelOptions.FirstOrDefault();

    private async Task<bool> EnsureKernelAsync(ModelOptionViewModel? option)
    {
        var (provider, modelId, apiKey) = RouteFor(option);
        var cwd = CurrentCwd();
        // RVM 不是默认后端：只有工作区显式启用隔离保护才准备会话
        var protect = IsRvmProtected;
        if (protect)
            await EnsureRvmWorkspaceAsync(cwd).ConfigureAwait(false);
        else
            await DisableRvmWorkspaceAsync().ConfigureAwait(false);

        var binding = protect ? RvmBinding() : null;
        var bindingKey = BindingKey(binding);
        var cwdChanged = _newConversationCwd
                         && _activeCwd is not null
                         && !string.Equals(cwd, _activeCwd, StringComparison.OrdinalIgnoreCase);
        if (_kernel.IsAvailable)
        {
            if (!cwdChanged
                && string.Equals(bindingKey, _startedRvmKey, StringComparison.Ordinal)
                && (option is null
                    || (string.Equals(provider, _activeProvider, StringComparison.Ordinal)
                        && string.Equals(modelId, _activeModel, StringComparison.Ordinal))))
            {
                _newConversationCwd = false;
                return true;
            }
            // 选中的模型、会话目录或 RVM 绑定与 runtime 不一致：只能重启
            await _kernel.ShutdownAsync().ConfigureAwait(false);
            lock (_gate) _openSessions.Clear();
        }

        var startedProvider = provider;
        var startedModel = modelId;
        var started = await _kernel.StartAsync(provider, modelId, cwd, apiKey, binding).ConfigureAwait(false);
        if (!started && option is null && (provider != "deepseek-official" || modelId != "deepseek-flash"))
        {
            // 本机目录里的占位模型不代表 DSH 路由，启动阶段用基线模型兜底；
            // 用户显式选择的 provider 不兜底，失败要如实反馈
            apiKey = _catalog.Providers.FirstOrDefault(p => p.Id == "deepseek")?.ApiKey;
            startedProvider = "deepseek-official";
            startedModel = "deepseek-flash";
            started = await _kernel.StartAsync(
                startedProvider, startedModel, cwd, apiKey, binding).ConfigureAwait(false);
        }
        if (!started) return false;
        _startedRvmKey = bindingKey;
        _activeProvider = startedProvider;
        _activeModel = startedModel;
        _activeCwd = cwd;
        _newConversationCwd = false;
        // 新的 node 进程里没有任何已打开会话，权限目录也要重新读取
        lock (_gate) _openSessions.Clear();
        ResetPermissions(keepCatalog: false);
        _ = LoadPermissionCatalogAsync();

        // 先落盘凭据再返回：首个 prompt 解析凭据时不能与写入赛跑
        if (!string.IsNullOrEmpty(apiKey))
            await _kernel.SetCredentialAsync(CredentialRefFor(startedProvider), apiKey).ConfigureAwait(false);

        // 目录发现可能走网络（官方 /models），不能挡住历史恢复与会话列表
        _ = ImportModelsAsync();
        return true;
    }

    private async Task ImportModelsAsync()
    {
        // 插件桥刚挂载时首次查询可能还拿不到凭据，空结果重试一次
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var models = await _kernel.ListModelsAsync().ConfigureAwait(false);
                if (models.Count > 0)
                {
                    _catalog.ApplyDshModels(models);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or JsonException)
            {
            }

            await Task.Delay(1500).ConfigureAwait(false);
        }
    }

    // 插件配置写进启动 patch 后，只有新内核进程能读到；显式重启以便插件页立即确认结果
    public async Task<KernelRestartResult> RestartKernelAsync()
    {
        lock (_gate)
        {
            if (_kernelAssistantId is not null)
                return new KernelRestartResult(false, "当前回复尚未结束，无法重启内核。");
        }

        await _kernel.ShutdownAsync().ConfigureAwait(false);
        _activeProvider = null;
        _activeModel = null;
        _activeCwd = null;

        var option = CurrentOption();
        if (option is null)
            return new KernelRestartResult(false, "没有可用模型，无法启动内核。");

        if (!await EnsureKernelAsync(option).ConfigureAwait(false))
            return new KernelRestartResult(false, $"内核重启失败：{_kernel.Status}");

        bool persisted;
        lock (_gate) persisted = _sessionPersisted;
        if (persisted && await _kernel.ResumeAsync(CurrentSessionId).ConfigureAwait(false))
        {
            lock (_gate) _openSessions.Add(_sessionId);
            await RefreshPermissionAsync(_sessionId).ConfigureAwait(false);
        }
        return new KernelRestartResult(true, "内核已重启，插件更改已生效。");
    }

    private async Task KernelSendAsync(string id, string prompt, ModelOptionViewModel? option)
    {
        // 每次发送都要过一遍：内核可能在跑，但路由或会话目录已经变了（SDK 只能在 initialize 时固定）
        if (!await EnsureKernelAsync(option).ConfigureAwait(false))
        {
            FailAssistant(id, string.IsNullOrWhiteSpace(_kernel.Status) ? "内核不可用" : _kernel.Status);
            return;
        }
        bool persisted;
        bool alreadyOpen;
        lock (_gate)
        {
            persisted = _sessionPersisted;
            alreadyOpen = _openSessions.Contains(_sessionId);
        }
        if (persisted && !alreadyOpen && await _kernel.ResumeAsync(CurrentSessionId).ConfigureAwait(false))
        {
            lock (_gate) _openSessions.Add(_sessionId);
            await RefreshPermissionAsync(_sessionId).ConfigureAwait(false);
        }
        await KernelPromptAsync(id, prompt);
    }

    private async Task KernelPromptAsync(string id, string prompt)
    {
        lock (_gate)
        {
            _kernelAssistantId = id;
            _lastTurnAssistantId = null;
            _committedText = string.Empty;
            _committedReasoning = string.Empty;
            _stepText = string.Empty;
            _stepReasoning = string.Empty;
            _streamOpen = false;
            _reasoningStarted = null;
        }
        RunningChanged?.Invoke(true);
        // 基线快照必须先于模型动手：turn 结束时才能只统计本轮的改动
        await _changeTracker.BeginAsync(CurrentCwd(), ++_turnSeq).ConfigureAwait(false);
        if (await _kernel.PromptAsync(CurrentSessionId, prompt).ConfigureAwait(false) is null)
        {
            FailAssistant(id, string.IsNullOrWhiteSpace(_kernel.Status) ? "发送失败" : _kernel.Status);
            return;
        }
        lock (_gate)
        {
            _sessionPersisted = true;
            _openSessions.Add(_sessionId);
        }
        // 会话在 prompt 时即落盘：立刻刷一次列表，新会话无需等到整轮结束才出现
        _ = RefreshConversationsAsync();
        // 新会话的权限由 permission-presets 在创建时播种，补拉一次让等级控件即时出现
        _ = RefreshPermissionIfMissingAsync(CurrentSessionId);
    }

    public async Task<BranchResult> BranchAsync(string messageId)
    {
        string parentId;
        long atSeq;
        lock (_gate)
        {
            if (_kernelAssistantId is not null) return new BranchResult(false, "当前回复尚未结束，无法创建分支。");
            var index = _messages.FindIndex(m => m.Id == messageId);
            if (index < 0) return new BranchResult(false, "找不到要分支的消息。");
            var message = _messages[index];
            if (message.BranchSeq is not { } seq) return new BranchResult(false, "该消息没有可用的分支点。");
            if (message.BranchUnavailable) return new BranchResult(false, "仅可从已完成轮次的最后一条消息分支。");
            parentId = _sessionId;
            atSeq = seq;
        }

        if (!await EnsureKernelAsync(null).ConfigureAwait(false))
            return new BranchResult(false, string.IsNullOrWhiteSpace(_kernel.Status) ? "内核不可用。" : _kernel.Status);
        var childId = await _kernel.ForkSessionAsync(parentId, atSeq).ConfigureAwait(false);
        if (childId is null)
            return new BranchResult(false, string.IsNullOrWhiteSpace(_kernel.Status) ? "分支失败。" : _kernel.Status);

        await RefreshConversationsAsync().ConfigureAwait(false);
        await ReloadSessionAsync(childId, resume: true).ConfigureAwait(false);
        return new BranchResult(true, string.Empty);
    }

    private (string Provider, string Model, string? ApiKey) RouteFor(ModelOptionViewModel? option)
    {
        // 直接走选中的模型对象：DSH 目录项与本机目录项可能同名，不能再按显示名反查
        option ??= _catalog.ModelOptions.FirstOrDefault(o => o.IsSelected)
                   ?? _catalog.ModelOptions.FirstOrDefault();
        if (option is null)
            return ("deepseek-official", "deepseek-flash", null);

        var provider = _catalog.Providers.FirstOrDefault(p => p.Name == option.ProviderName)
                       ?? _catalog.Providers.FirstOrDefault(p => p.Id == option.ProviderRoute)
                       ?? _catalog.Providers.FirstOrDefault(p => LocalRoute(p.Id) == option.ProviderRoute);
        if (!string.IsNullOrWhiteSpace(option.ProviderRoute))
            return (option.ProviderRoute,
                string.IsNullOrWhiteSpace(option.ModelId) ? option.DisplayName : option.ModelId,
                provider?.ApiKey);

        var id = provider?.Id ?? "deepseek";
        return (id == "deepseek" ? "deepseek-official" : id, option.DisplayName, provider?.ApiKey);
    }

    private static string LocalRoute(string providerId) =>
        providerId == "deepseek" ? "deepseek-official" : providerId;

    // 仅当叠加盘会话已经真实建立、且引擎连接参数齐全时才接管 shell；
    // 任一缺失都返回 null，让内核保留 DSH 自己的沙箱与“RVM 不可用”提示
    private DshRvmBinding? RvmBinding()
    {
        if (_rvmSessionId == 0 || _rvmWorkspaceRoot is null) return null;
        var engine = _rvm.EnginePath;
        var pipe = _rvm.PipeName;
        var token = _rvm.Token;
        if (engine is null || pipe is null || token is null) return null;
        return new DshRvmBinding(engine, pipe, token, _rvmSessionId, _rvmWorkspaceRoot);
    }

    // 绑定变化（例如首次开启 RVM 隔离保护）必须触发内核重启，否则新进程拿不到 RESPONSIBLE_RVM_TOKEN
    private static string? BindingKey(DshRvmBinding? binding) => binding is null
        ? null
        : string.Join('|', binding.EnginePath, binding.PipeName, binding.Token,
            binding.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.WorkspaceRoot);

    private static string CredentialRefFor(string provider)
    {
        var id = provider == "deepseek-official" ? "deepseek" : provider;
        return id switch
        {
            "deepseek" => "DEEPSEEK_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            "kimi" => "MOONSHOT_API_KEY",
            "google" => "GEMINI_API_KEY",
            "azure" => "AZURE_API_KEY",
            "amazon-bedrock" => "AWS_BEARER_TOKEN_BEDROCK",
            _ => "DSH_API_KEY",
        };
    }
}
