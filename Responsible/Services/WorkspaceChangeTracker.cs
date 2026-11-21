using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Responsible.Services;

// 按 turn 采集工作区改动：用临时 index/对象库在 turn 起止各写一棵 tree，再 diff --numstat。
// 仓库自身的 index、对象和工作区不被写入；非 git 目录直接跳过（不生成卡片）。
public sealed class WorkspaceChangeTracker : IWorkspaceChangeTracker
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private string? _root;
    private string? _temp;
    private string? _gitDir;
    private string? _before;
    private string _cwd = "";
    private int _turn;
    private bool _active;

    public async Task BeginAsync(string cwd, int turn)
    {
        Cleanup();
        var root = await GitAsync(cwd, ["rev-parse", "--show-toplevel"]).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root)) return;
        root = root.Trim();

        var gitDir = await GitAsync(cwd, ["rev-parse", "--absolute-git-dir"]).ConfigureAwait(false);
        gitDir = string.IsNullOrWhiteSpace(gitDir) ? Path.Combine(root, ".git") : gitDir.Trim();

        var temp = Path.Combine(Path.GetTempPath(), "Responsible-changes", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(temp, "objects"));
        var repoIndex = Path.Combine(gitDir, "index");
        if (File.Exists(repoIndex)) File.Copy(repoIndex, Path.Combine(temp, "index"), true);

        var env = EnvFor(temp, gitDir);
        var before = await SnapshotAsync(root, env).ConfigureAwait(false);
        if (before is null)
        {
            SafeDelete(temp);
            return;
        }

        lock (_gate)
        {
            _root = root;
            _temp = temp;
            _gitDir = gitDir;
            _before = before;
            _cwd = cwd;
            _turn = turn;
            _active = true;
        }
    }

    public async Task<ChatChanges?> EndAsync()
    {
        string root, temp, gitDir, before, cwd;
        int turn;
        lock (_gate)
        {
            if (!_active || _root is null || _temp is null || _gitDir is null || _before is null) return null;
            root = _root;
            temp = _temp;
            gitDir = _gitDir;
            before = _before;
            cwd = _cwd;
            turn = _turn;
            _active = false;
            _root = null;
            _temp = null;
            _gitDir = null;
            _before = null;
        }

        try
        {
            var env = EnvFor(temp, gitDir);
            var after = await SnapshotAsync(root, env).ConfigureAwait(false);
            if (after is null) return null;
            var numstat = await GitAsync(root, ["diff-tree", "-r", "-M", "--numstat", before, after], env).ConfigureAwait(false);
            if (numstat is null) return null;

            var files = new List<ChangedFile>();
            var added = 0;
            var deleted = 0;
            foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 3) continue;
                var binary = parts[0] == "-" || parts[1] == "-";
                var a = binary ? 0 : ParseInt(parts[0]);
                var d = binary ? 0 : ParseInt(parts[1]);
                var path = string.Join('\t', parts[2..]);
                files.Add(new ChangedFile(path, path, a, d, binary, false));
                added += a;
                deleted += d;
            }
            if (files.Count == 0) return null;
            files.Sort(static (x, y) => string.CompareOrdinal(x.Display, y.Display));
            return new ChatChanges(turn, cwd, files.Count, added, deleted, files);
        }
        finally
        {
            SafeDelete(temp);
        }
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    private static Dictionary<string, string> EnvFor(string temp, string gitDir) => new()
    {
        ["GIT_INDEX_FILE"] = Path.Combine(temp, "index"),
        ["GIT_OBJECT_DIRECTORY"] = Path.Combine(temp, "objects"),
        ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = Path.Combine(gitDir, "objects"),
    };

    private static async Task<string?> SnapshotAsync(string root, Dictionary<string, string> env)
    {
        if (await GitAsync(root, ["add", "--all", "--ignore-errors"], env, allowExitOne: true).ConfigureAwait(false) is null)
            return null;
        var tree = await GitAsync(root, ["write-tree"], env).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(tree) ? null : tree.Trim();
    }

    private static async Task<string?> GitAsync(
        string cwd,
        string[] args,
        Dictionary<string, string>? env = null,
        bool allowExitOne = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(cwd);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (env is not null)
            foreach (var (key, value) in env) psi.Environment[key] = value;

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            using (var timeout = new CancellationTokenSource(GitTimeout))
            {
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch (InvalidOperationException) { }
                    return null;
                }
            }
            _ = await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 || (allowExitOne && process.ExitCode == 1) ? stdout : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void Cleanup()
    {
        string? temp;
        lock (_gate)
        {
            temp = _temp;
            _temp = null;
            _root = null;
            _gitDir = null;
            _before = null;
            _active = false;
        }
        if (temp is not null) SafeDelete(temp);
    }

    private static void SafeDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // 临时快照目录清理失败不影响会话
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：留给操作系统临时目录清理
        }
    }
}
