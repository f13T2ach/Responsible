using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Responsible.Services;

public sealed class RvmPolicyService : IRvmPolicyService
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible", "rvm-policy.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private RvmPolicy _current;

    public RvmPolicyService()
    {
        _current = Load(out var loadError);
        LoadError = loadError;
    }

    public RvmPolicy Current
    {
        get { lock (_lock) return _current; }
    }

    public string? LoadError { get; }

    public void Save(RvmPolicy policy)
    {
        lock (_lock)
        {
            _current = policy with
            {
                Devices = policy.Devices
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => PortNumber(name))
                    .ToArray(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(
                StorePath,
                JsonSerializer.Serialize(new PolicyRecord(
                    _current.NetworkEnabled, _current.DevicesEnabled, _current.Devices), JsonOptions));
        }
    }

    public IReadOnlyList<string> EnumerateSerialPorts()
    {
        var ports = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not string port) continue;
                    var trimmed = port.Trim();
                    if (trimmed.Length == 0) continue;
                    ports.Add(trimmed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return ports
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ToArray();
    }

    private static int PortNumber(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)) trimmed = trimmed[4..];
        return trimmed.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(trimmed.AsSpan(3), out var number)
            ? number
            : int.MaxValue;
    }

    private static RvmPolicy Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(StorePath)) return RvmPolicy.Default;
            var record = JsonSerializer.Deserialize<PolicyRecord>(
                File.ReadAllText(StorePath), JsonOptions);
            if (record is null) return RvmPolicy.Default;
            return new RvmPolicy(
                record.NetworkEnabled,
                record.DevicesEnabled,
                (record.Devices ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray(),
                record.JailbreakTest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Failed to read the RVM penetration policy: {ex.Message}";
            return RvmPolicy.Default;
        }
    }

    private sealed record PolicyRecord(
        bool NetworkEnabled,
        bool DevicesEnabled,
        IReadOnlyList<string>? Devices,
        bool JailbreakTest = false);
}