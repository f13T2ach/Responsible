using System.IO;
using System.Net.Http;
using System.Text.Json;
using Responsible.Localization;
using Responsible.ViewModel;

namespace Responsible.Services;

public sealed partial class ChatSessionService
{
    private ModelOptionViewModel? CurrentOption() =>
        _catalog.ModelOptions.FirstOrDefault(o => o.IsSelected) ?? _catalog.ModelOptions.FirstOrDefault();

    // 启动与打开会话会并发调到这里：不串行化就会同时起两个 node 内核，
    // 打开一个对话要等两轮内核启动。
    private readonly SemaphoreSlim _kernelGate = new(1, 1);

    private async Task<bool> EnsureKernelAsync(ModelOptionViewModel? option, bool deferRvm = false)
    {
        await _kernelGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await EnsureKernelCoreAsync(option, deferRvm).ConfigureAwait(false);
        }
        finally
        {
            _kernelGate.Release();
        }
    }

    private async Task<bool> EnsureKernelCoreAsync(ModelOptionViewModel? option, bool deferRvm)
    {
        var (provider, modelId, apiKey) = RouteFor(option);
        var cwd = CurrentCwd();
        // RVM 不是默认后端：只有工作区显式启用隔离保护才准备会话。
        // deferRvm：启动/打开会话时先起内核，把会话盘交给后台预热（见 WarmRvmSessionAsync），
        // 否则 10 秒的准备会一律算在“打开会话”的等待里。
        var protect = IsRvmProtected && !deferRvm;
        if (protect)
        {
            // 建会话盘可能要十几秒（首次还要建索引/播种），不能让它挡住内核启动和会话列表；
            // 超时就先带着“RVM 未就绪”把内核起起来，后续预热完成会因绑定变化重启内核。
            var ensure = EnsureRvmWorkspaceAsync(cwd);
            if (await Task.WhenAny(ensure, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false) == ensure)
                await ensure.ConfigureAwait(false);
            else
                _ = ensure;  // 后台继续；失败原因会写进 badge
        }
        else if (!deferRvm)
        {
            await DisableRvmWorkspaceAsync().ConfigureAwait(false);
        }

        var binding = protect ? RvmBinding() : null;
        // 工作区要求了隔离但会话没建起来：把真实原因交给内核，让 shell 以这条理由拒绝执行，
        // 而不是回退到 DSH 的 ACL 沙箱后报一个误导性的权限错误。
        var rvmUnavailable = protect && binding is null
            ? (_rvm.Status is { Length: > 0 } status ? status : "the RVM session is not established")
            : null;
        var bindingKey = BindingKey(binding, rvmUnavailable);
        // 会话目录一变就必须重启内核：patch 里的 sandbox 后端是按工作区生成的，
        // 带着 RVM 的插件跑在普通工作区上会直接拒绝执行命令。
        var cwdChanged = _activeCwd is not null
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
        var started = await _kernel.StartAsync(provider, modelId, cwd, apiKey, binding, rvmUnavailable).ConfigureAwait(false);
        if (!started && option is null && (provider != "deepseek-official" || modelId != "deepseek-flash"))
        {
            // 本机目录里的占位模型不代表 DSH 路由，启动阶段用基线模型兜底；
            // 用户显式选择的 provider 不兜底，失败要如实反馈
            apiKey = _catalog.Providers.FirstOrDefault(p => p.Id == "deepseek")?.ApiKey;
            startedProvider = "deepseek-official";
            startedModel = "deepseek-flash";
            started = await _kernel.StartAsync(
                startedProvider, startedModel, cwd, apiKey, binding, rvmUnavailable).ConfigureAwait(false);
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
                return new KernelRestartResult(false, "The current reply has not finished; the kernel cannot be restarted.");
        }

        await _kernel.ShutdownAsync().ConfigureAwait(false);
        _activeProvider = null;
        _activeModel = null;
        _activeCwd = null;

        var option = CurrentOption();
        if (option is null)
            return new KernelRestartResult(false, "No model is available; the kernel cannot start.");

        if (!await EnsureKernelAsync(option).ConfigureAwait(false))
            return new KernelRestartResult(false, $"Kernel restart failed: {_kernel.Status}");

        bool persisted;
        lock (_gate) persisted = _sessionPersisted;
        if (persisted && await _kernel.ResumeAsync(CurrentSessionId).ConfigureAwait(false))
        {
            lock (_gate) _openSessions.Add(_sessionId);
            await RefreshPermissionAsync(_sessionId).ConfigureAwait(false);
        }
        return new KernelRestartResult(true, Loc.T("L.Plugins.KernelRestarted"));
    }

    private async Task KernelSendAsync(string id, string prompt, ModelOptionViewModel? option)
    {
        // 每次发送都要过一遍：内核可能在跑，但路由或会话目录已经变了（SDK 只能在 initialize 时固定）
        if (!await EnsureKernelAsync(option).ConfigureAwait(false))
        {
            FailAssistant(id, string.IsNullOrWhiteSpace(_kernel.Status) ? "Kernel unavailable" : _kernel.Status);
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
        // 基线快照必须先于模型动手：turn 结束时才能只统计本轮的改动。
        // 受保护会话的写入落在会话盘上，改动统计也要以会话盘为根。
        var trackingRoot = IsRvmProtected && _rvmVirtualRoot is not null
            ? _rvmVirtualRoot
            : CurrentCwd();
        // 会话盘是引擎自己的副本，tracker 可以直接用它的 index 做增量，
        // 否则每轮都要把整盘 1.5 万文件重新 hash（实测 65 秒才轮到模型）。
        await _changeTracker.BeginAsync(trackingRoot, ++_turnSeq,
            IsRvmProtected && _rvmVirtualRoot is not null).ConfigureAwait(false);
        if (await _kernel.PromptAsync(CurrentSessionId, prompt).ConfigureAwait(false) is null)
        {
            FailAssistant(id, string.IsNullOrWhiteSpace(_kernel.Status) ? "Send failed" : _kernel.Status);
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
            if (_kernelAssistantId is not null) return new BranchResult(false, "The current reply has not finished; a branch cannot be created.");
            var index = _messages.FindIndex(m => m.Id == messageId);
            if (index < 0) return new BranchResult(false, "The message to branch from was not found.");
            var message = _messages[index];
            if (message.BranchSeq is not { } seq) return new BranchResult(false, "This message has no branch point.");
            if (message.BranchUnavailable) return new BranchResult(false, "Branches can only be created from the last message of a completed turn.");
            parentId = _sessionId;
            atSeq = seq;
        }

        if (!await EnsureKernelAsync(null).ConfigureAwait(false))
            return new BranchResult(false, string.IsNullOrWhiteSpace(_kernel.Status) ? "Kernel unavailable." : _kernel.Status);
        var childId = await _kernel.ForkSessionAsync(parentId, atSeq).ConfigureAwait(false);
        if (childId is null)
            return new BranchResult(false, string.IsNullOrWhiteSpace(_kernel.Status) ? "Branch failed." : _kernel.Status);

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

    // 仅当会话盘已经真实建立、且引擎连接参数齐全时才接管 shell；
    // 任一缺失都返回 null，让内核保留 DSH 自己的沙箱与“RVM 不可用”提示
    private DshRvmBinding? RvmBinding()
    {
        if (_rvmSessionId == 0 || _rvmWorkspaceRoot is null || _rvmVirtualRoot is null) return null;
        var engine = _rvm.EnginePath;
        var pipe = _rvm.PipeName;
        var token = _rvm.Token;
        if (engine is null || pipe is null || token is null) return null;
        RvmTrace($"binding: jailbreak={_rvmPolicy.Current.JailbreakTest} loadError={_rvmPolicy.LoadError ?? "none"}");
        return new DshRvmBinding(engine, pipe, token, _rvmSessionId, _rvmVirtualRoot,
            _rvmWorkspaceRoot, _rvmPolicy.Current.JailbreakTest);
    }

    // 绑定变化（例如首次开启 RVM 隔离保护）必须触发内核重启，否则新进程拿不到 RESPONSIBLE_RVM_TOKEN；
    // 会话没建起来时把失败原因也编进 key，原因变了同样重启，让内核拿到新的拒绝理由。
    private static string? BindingKey(DshRvmBinding? binding, string? unavailable) => binding is null
        ? unavailable
        : string.Join('|', binding.EnginePath, binding.PipeName, binding.Token,
            binding.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.WorkspaceRoot, binding.HostWorkspaceRoot);

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
