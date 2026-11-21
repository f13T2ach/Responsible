namespace Responsible.Services;

public sealed record WinFspProbe(bool Installed, string Version, string InstallDir);

public sealed record WinFspInstallResult(bool Success, int ExitCode, string Message);

public interface IWinFspService
{
    WinFspProbe Probe();

    string DriveLetter { get; }

    bool TrySetDriveLetter(string value, out string? error);

    IReadOnlyList<string> AvailableDriveLetters();

    Task<WinFspInstallResult> InstallAsync();
}