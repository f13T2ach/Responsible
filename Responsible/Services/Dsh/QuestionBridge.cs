using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace Responsible.Services.Dsh;

/// <summary>
/// 提问问询的宿主侧：轮询内核的 /questions，把模型的问题交给界面作答。
/// 与审批不同，提问不涉及权限，RVM 会话照常显示；只有不是当前会话的请求会被交回内核。
/// </summary>
public sealed class QuestionBridge : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly IDshKernelService _kernel;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, bool> _answered = new(StringComparer.Ordinal);
    private bool _polling;
    private bool _disposed;
    private string _lastNote = "";

    public QuestionBridge(IDshKernelService kernel)
    {
        _kernel = kernel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>待用户回答的问题；不是当前会话的不会出现在这里。</summary>
    public event Action<IReadOnlyList<DshQuestionRow>>? PendingChanged;

    public IReadOnlyList<DshQuestionRow> Pending { get; private set; } = [];

    /// <summary>只有当前会话的问题才弹卡片。</summary>
    public Func<string, bool> IsCurrentSession { get; set; } = _ => true;

    public void Start()
    {
        Log("start()");
        _timer.Start();
    }

    /// <summary>提交一份答案：answers 与插件的 wire 结构一致。</summary>
    public async Task AnswerAsync(string id, object answers)
    {
        var bridge = _kernel.Bridge;
        if (bridge is null) return;
        _answered[id] = true;
        Log($"answer id={id}");
        try
        {
            var ok = await DshBridgeHttpClient.AnswerQuestionAsync(bridge, id, answers, CancellationToken.None)
                .ConfigureAwait(false);
            Log($"answer posted ok={ok}");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            Log($"answer failed: {ex.GetType().Name} {ex.Message}");
        }
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
            var rows = await DshBridgeHttpClient.ReadQuestionsAsync(bridge, CancellationToken.None)
                .ConfigureAwait(false);
            var live = new List<DshQuestionRow>();
            foreach (var row in rows)
            {
                if (_answered.ContainsKey(row.Id)) continue;
                if (!IsCurrentSession(row.SessionId))
                {
                    // 不是当前会话：不弹，等用户切回去再接（这里只是不展示）
                    live.Add(row);
                    continue;
                }
                live.Add(row);
            }

            Note(live.Count > 0 ? $"{live.Count} pending" : "");
            if (!live.Select(item => item.Id).SequenceEqual(Pending.Select(item => item.Id)))
            {
                Pending = live;
                PendingChanged?.Invoke(Pending);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            Log($"poll failed: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _polling = false;
        }
    }

    private void Note(string note)
    {
        if (note == _lastNote) return;
        _lastNote = note;
        if (note.Length > 0) Log("poll: " + note);
    }

    private static void Log(string line)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Responsible", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "questions.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
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
