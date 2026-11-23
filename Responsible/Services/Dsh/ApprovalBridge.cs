using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace Responsible.Services.Dsh;

/// <summary>
/// 审批问询的宿主侧：轮询内核的 /approvals，把要用户决定的问题交给界面。
/// 两条硬规则：
/// 1. RVM 会话直接拒绝——用户选隔离就是不愿放开，连问都不问；
/// 2. 不响应、超时、未知 id 一律按 unavailable（fail-closed），绝不默认允许。
/// </summary>
public sealed class ApprovalBridge : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly IDshKernelService _kernel;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, string> _answered = new(StringComparer.Ordinal);
    private bool _polling;
    private string _lastNote = "";
    private bool _disposed;

    public ApprovalBridge(IDshKernelService kernel)
    {
        _kernel = kernel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>待用户决定的请求；RVM 会话的请求不会出现在这里（已被直接拒绝）。</summary>
    public event Action<IReadOnlyList<DshApprovalRow>>? PendingChanged;

    /// <summary>请求被宿主自行处置（RVM 拒绝、非当前会话、失效），用于状态栏提示。</summary>
    public event Action<string>? Decided;

    public IReadOnlyList<DshApprovalRow> Pending { get; private set; } = [];

    public void Start()
    {
        Log("start()");
        _timer.Start();
    }

    /// <summary>当前会话是否处在 RVM 隔离里——是的话所有审批一律拒绝。</summary>
    public Func<string, bool> IsRvmSession { get; set; } = _ => false;

    /// <summary>非本会话的请求交回内核（unavailable），避免弹到别的对话上。</summary>
    public Func<string, bool> IsCurrentSession { get; set; } = _ => true;

    public async Task AnswerAsync(string id, string outcome)
    {
        var bridge = _kernel.Bridge;
        if (bridge is null) return;
        _answered[id] = outcome;
        Log($"answer id={id} outcome={outcome}");
        try
        {
            await DshBridgeHttpClient.AnswerApprovalAsync(bridge, id, outcome, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
        }
        Decided?.Invoke(id);
        Pending = Pending.Where(item => item.Id != id).ToArray();
        PendingChanged?.Invoke(Pending);
    }

    private async Task PollAsync()
    {
        if (_polling || _disposed) return;
        var bridge = _kernel.Bridge;
        if (bridge is null) { Note("no bridge"); return; }
        if (!_kernel.IsAvailable) { Note("kernel not available"); return; }
        Note("");
        _polling = true;
        try
        {
            var rows = await DshBridgeHttpClient.ReadApprovalsAsync(bridge, CancellationToken.None)
                .ConfigureAwait(false);
            Note(rows.Count > 0 ? $"{rows.Count} pending" : "");
            var live = new List<DshApprovalRow>();
            foreach (var row in rows)
            {
                if (_answered.ContainsKey(row.Id)) continue;
                // 不是当前会话的请求：交回内核，别弹给用户
                if (!IsCurrentSession(row.SessionId))
                {
                    await AnswerAsync(row.Id, "unavailable").ConfigureAwait(false);
                    continue;
                }
                // RVM 会话：隔离里没有“完全权限”这回事，直接拒绝
                if (IsRvmSession(row.SessionId))
                {
                    Log($"rvm-reject id={row.Id} tool={row.ToolName} reason={row.Reason}");
                    await AnswerAsync(row.Id, "rejected").ConfigureAwait(false);
                    continue;
                }
                live.Add(row);
            }

            if (!live.Select(item => item.Id).SequenceEqual(Pending.Select(item => item.Id)))
            {
                Pending = live;
                PendingChanged?.Invoke(Pending);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            // 内核没起来或桥断了：下一轮再试
            Log($"poll failed: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    // 状态只在变化时记一行，避免每 400ms 刷一次
    private void Note(string note)
    {
        if (note == _lastNote) return;
        _lastNote = note;
        if (note.Length > 0) Log("poll: " + note);
    }

    // 审批链路的黑匣子：RVM 拒绝、用户回答、超时都留一行
    private static void Log(string line)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "approval.log"),
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
