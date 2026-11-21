using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Responsible.Services;

public sealed class WinFspService : IWinFspService
{
    private const string WinFspMsiFileName = "winfsp-2.2.26323.msi";

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible",
        "winfsp.json");

    private static readonly string InstallerPath = Path.Combine(
        AppContext.BaseDirectory,
        "ThirdParty",
        "WinFsp",
        WinFspMsiFileName);

    private static readonly char ReservedDrive =
        char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)![0]);

    private string _driveLetter;

    public WinFspService() => _driveLetter = LoadDriveLetter();

    public string DriveLetter => _driveLetter;

    public WinFspProbe Probe()
    {
        var installDir = ReadInstallDir();
        foreach (var candidate in CandidateLibraries(installDir))
        {
            if (!File.Exists(candidate)) continue;
            var version = FileVersionInfo.GetVersionInfo(candidate).FileVersion;
            return new WinFspProbe(true, string.IsNullOrWhiteSpace(version) ? "unknown" : version,
                installDir);
        }

        return new WinFspProbe(false, string.Empty, installDir);
    }

    public bool TrySetDriveLetter(string value, out string? error)
    {
        error = null;
        var trimmed = (value ?? string.Empty).Trim().TrimEnd(':').ToUpperInvariant();
        if (trimmed.Length != 1 || trimmed[0] < 'A' || trimmed[0] > 'Z')
        {
            error = "盘符必须是 A–Z 中的单个字母。";
            return false;
        }

        if (trimmed[0] == ReservedDrive)
        {
            error = $"{trimmed}: 是系统盘，不能用于 WinFsp 虚拟盘。";
            return false;
        }

        _driveLetter = trimmed;
        SaveDriveLetter();
        return true;
    }

    public IReadOnlyList<string> AvailableDriveLetters()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        used.Add(ReservedDrive);

        return Enumerable.Range('A', 'Z' - 'A' + 1)
            .Select(code => ((char)code).ToString())
            .Where(letter => !used.Contains(letter[0]) || letter == _driveLetter)
            .ToList();
    }

    public async Task<WinFspInstallResult> InstallAsync()
    {
        if (!File.Exists(InstallerPath))
            return new WinFspInstallResult(false, -1, $"未找到随应用分发的 WinFsp 安装包：{InstallerPath}");

        var start = new ProcessStartInfo("msiexec.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        start.ArgumentList.Add("/i");
        start.ArgumentList.Add(InstallerPath);
        start.ArgumentList.Add("/qb");
        start.ArgumentList.Add("/norestart");

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new WinFspInstallResult(false, -1, "无法启动 WinFsp 安装程序。");

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new WinFspInstallResult(true, 0, string.Empty)
                : new WinFspInstallResult(false, process.ExitCode,
                    $"WinFsp 安装程序退出码 {process.ExitCode}，安装未完成。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new WinFspInstallResult(false, -1, "安装已取消：需要管理员权限才能安装 WinFsp。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new WinFspInstallResult(false, -1, $"WinFsp 安装失败：{ex.Message}");
        }
    }

    private static IEnumerable<string> CandidateLibraries(string installDir)
    {
        if (!string.IsNullOrEmpty(installDir))
            yield return Path.Combine(installDir, "bin", "winfsp-x64.dll");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winfsp-x64.dll");
        yield return @"C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll";
    }

    private static string ReadInstallDir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
            if (key?.GetValue("InstallDir") is string installDir && installDir.Length > 0)
                return installDir.TrimEnd('\\');
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return string.Empty;
    }

    private static string LoadDriveLetter()
    {
        try
        {
            if (File.Exists(ConfigPath)
                && JsonSerializer.Deserialize<WinFspRecord>(File.ReadAllText(ConfigPath)) is { DriveLetter: { } letter }
                && letter.Length == 1 && letter[0] >= 'A' && letter[0] <= 'Z'
                && letter[0] != ReservedDrive)
            {
                return letter;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return "R";
    }

    private void SaveDriveLetter()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new WinFspRecord(_driveLetter)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record WinFspRecord(string DriveLetter);
}