using System.Diagnostics;
using System.IO;
using System.Text;

namespace Responsible.Services;

public enum RvmIndexState
{
    Ready,
    MissingRepository,
    MissingIndex,
    GitUnavailable,
}

public sealed record RvmIndexStatus(RvmIndexState State, int IndexedFiles, string Message)
{
    public bool IsReady => State == RvmIndexState.Ready;

    public bool RequiresIndexing => State is RvmIndexState.MissingRepository or RvmIndexState.MissingIndex;
}

// RVM 会话盘的快照范围由工作区 Git 索引决定：索引之外（依赖目录、构建产物、
// 缓存）不会被枚举或复制。这里负责检查索引，并提供“普通索引 / 全面索引”两个
// 显式动作；不会扫描磁盘，只调用 git 自身。
public static class RvmWorkspaceIndex
{
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(15);

    public static async Task<RvmIndexStatus> InspectAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new RvmIndexStatus(RvmIndexState.MissingRepository, 0, "Workspace folder does not exist.");

        var inside = await RunGitAsync(directory, ["rev-parse", "--is-inside-work-tree"],
            InspectTimeout, cancellationToken).ConfigureAwait(false);
        if (inside is null)
            return new RvmIndexStatus(RvmIndexState.GitUnavailable, 0,
                "git.exe was not found; install Git and try again.");
        if (inside.ExitCode != 0 || !string.Equals(inside.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return new RvmIndexStatus(RvmIndexState.MissingRepository, 0,
                "This folder is not a Git repository yet.");

        var indexPath = await RunGitAsync(directory, ["rev-parse", "--git-path", "index"],
            InspectTimeout, cancellationToken).ConfigureAwait(false);
        var relative = indexPath?.Output.Trim();
        if (indexPath is null || indexPath.ExitCode != 0 || string.IsNullOrEmpty(relative))
            return new RvmIndexStatus(RvmIndexState.MissingIndex, 0, "Unable to read the Git index location.");
        var resolved = Path.IsPathRooted(relative)
            ? relative
            : Path.GetFullPath(Path.Combine(directory, relative));
        if (!File.Exists(resolved))
            return new RvmIndexStatus(RvmIndexState.MissingIndex, 0, "The Git repository has no index yet.");

        var listed = await RunGitAsync(directory, ["ls-files", "-z"], InspectTimeout,
            cancellationToken).ConfigureAwait(false);
        if (listed is null || listed.ExitCode != 0)
            return new RvmIndexStatus(RvmIndexState.MissingIndex, 0, "Unable to read the Git index.");
        var count = listed.Output.Count(ch => ch == '\0');
        if (count == 0)
            return new RvmIndexStatus(RvmIndexState.MissingIndex, 0, "The Git index contains no files.");
        return new RvmIndexStatus(RvmIndexState.Ready, count, $"Git index ready · {count} files.");
    }

    public static async Task<RvmIndexStatus> InitializeAsync(
        string directory,
        bool comprehensive,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new RvmIndexStatus(RvmIndexState.MissingRepository, 0, "Workspace folder does not exist.");

        var current = await InspectAsync(directory, cancellationToken).ConfigureAwait(false);
        if (current.State == RvmIndexState.GitUnavailable) return current;
        if (current.State == RvmIndexState.MissingRepository)
        {
            var init = await RunGitAsync(directory, ["init", "-q"], InspectTimeout,
                cancellationToken).ConfigureAwait(false);
            if (init is null)
                return new RvmIndexStatus(RvmIndexState.GitUnavailable, 0,
                    "git.exe was not found; install Git and try again.");
            if (init.ExitCode != 0)
                return new RvmIndexStatus(RvmIndexState.MissingIndex, 0,
                    $"git init failed: {FirstLine(init.Error)}");
        }

        await EnsureTempExcludedAsync(directory, cancellationToken).ConfigureAwait(false);

        // “全面索引”面向没有代码的办公工作区：连 .gitignore 里的文件也索引，
        // 因为用户期望整个文件夹都进入快照。普通索引仍尊重忽略规则。
        string[] arguments = comprehensive
            ? ["-c", "core.autocrlf=false", "add", "-A", "-f", "."]
            : ["-c", "core.autocrlf=false", "add", "-A", "."];
        var add = await RunGitAsync(directory, arguments, IndexTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (add is null)
            return new RvmIndexStatus(RvmIndexState.GitUnavailable, 0,
                "git.exe was not found; install Git and try again.");
        if (add.ExitCode != 0)
            return new RvmIndexStatus(RvmIndexState.MissingIndex, 0,
                $"Failed to build the Git index: {FirstLine(add.Error)}");

        return await InspectAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTempExcludedAsync(string directory,
        CancellationToken cancellationToken)
    {
        var path = await RunGitAsync(directory, ["rev-parse", "--git-path", "info/exclude"],
            InspectTimeout, cancellationToken).ConfigureAwait(false);
        var relative = path?.Output.Trim();
        if (path is null || path.ExitCode != 0 || string.IsNullOrEmpty(relative)) return;
        var resolved = Path.IsPathRooted(relative)
            ? relative
            : Path.GetFullPath(Path.Combine(directory, relative));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            var existing = File.Exists(resolved) ? await File.ReadAllTextAsync(resolved, cancellationToken)
                .ConfigureAwait(false) : string.Empty;
            if (existing.Contains(".rvm_temp", StringComparison.OrdinalIgnoreCase)) return;
            var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
            await File.AppendAllTextAsync(resolved, separator + ".rvm_temp/\n", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<GitResult?> RunGitAsync(
        string directory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errorTask = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new GitResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return new GitResult(-1, string.Empty, "git operation timed out");
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "unknown error" : line.Trim();
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}