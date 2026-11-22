using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Responsible;

/// <summary>
/// 宿主被强杀时的看护：拉起 RTray.exe 并把「我还活着」的心跳、以及「正常退出」的告别事件交给它。
/// RTray 不在同一条生命线上——它只看进程句柄和事件，所以宿主被 TerminateProcess 时它仍然活着，
/// 能替宿主清掉 subst 盘符、结束孤儿进程，并写一份崩溃报告。
/// </summary>
internal sealed class CrashTray : IDisposable
{
    private readonly Process? _process;
    private readonly EventWaitHandle? _bye;
    private readonly Timer? _heartbeat;
    private readonly string? _heartbeatPath;

    private CrashTray(Process? process, EventWaitHandle? bye, Timer? heartbeat, string? heartbeatPath)
    {
        _process = process;
        _bye = bye;
        _heartbeat = heartbeat;
        _heartbeatPath = heartbeatPath;
    }

    /// <summary>启动看护；找不到 RTray.exe 时安静跳过，宿主功能不受影响。</summary>
    public static CrashTray? Start(string localAppRoot)
    {
        var executable = LocateExecutable();
        if (executable is null) return null;

        var pid = Environment.ProcessId;
        var byeName = $"Responsible-Bye-{pid}";
        var logs = Path.Combine(localAppRoot, "logs");
        var heartbeatPath = Path.Combine(logs, "host-heartbeat");

        EventWaitHandle? bye = null;
        try
        {
            Directory.CreateDirectory(logs);
            bye = new EventWaitHandle(false, EventResetMode.ManualReset, byeName);
            File.WriteAllText(heartbeatPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            bye?.Dispose();
            return null;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--watch");
        start.ArgumentList.Add(pid.ToString());
        start.ArgumentList.Add("--bye-event");
        start.ArgumentList.Add(byeName);
        start.ArgumentList.Add("--heartbeat");
        start.ArgumentList.Add(heartbeatPath);
        start.ArgumentList.Add("--cleanup-subst");
        start.ArgumentList.Add(Path.Combine(localAppRoot, "RVM"));
        start.ArgumentList.Add("--report-dir");
        start.ArgumentList.Add(logs);

        Process? process = null;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 拉不起来就退化成宿主自己的启动清理，不影响本次运行
        }

        if (process is null)
        {
            bye.Dispose();
            return null;
        }

        // 心跳每 5 秒一次：报告里的 heartbeat_age_ms 用它判断最后一次“还活着”是什么时候
        var heartbeat = new Timer(_ =>
        {
            try
            {
                File.WriteAllText(heartbeatPath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        return new CrashTray(process, bye, heartbeat, heartbeatPath);
    }

    /// <summary>正常退出：置位告别事件，RTray 记录后自行结束。</summary>
    public void SayGoodbye()
    {
        try
        {
            _bye?.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _bye?.Dispose();
        _process?.Dispose();
    }

    // 发布时 RTray.exe 与主程序并列；开发期回落到源码树里的构建产物。
    private static string? LocateExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "RTray.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "RTray", "build", "bin",
                "RTray.exe")),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return null;
    }
}
