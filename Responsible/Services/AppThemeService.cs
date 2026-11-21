using System.IO;
using System.Text.Json;
using System.Windows;
using Wpf.Ui.Appearance;

namespace Responsible.Services;

public sealed class AppThemeService : IAppThemeService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Responsible",
        "theme.json");

    private Window? _window;
    private ThemeMode _mode;

    public AppThemeService()
    {
        _mode = Load();
        ApplyCore();
    }

    public ThemeMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Save();
            ApplyCore();
        }
    }

    public void Attach(Window window)
    {
        _window = window;
        if (_mode == ThemeMode.System)
            SystemThemeWatcher.Watch(window);
    }

    private void ApplyCore()
    {
        if (_mode == ThemeMode.System)
        {
            if (_window is { IsLoaded: true })
                SystemThemeWatcher.Watch(_window);
            else
                ApplicationThemeManager.ApplySystemTheme();
        }
        else
        {
            if (_window is { IsLoaded: true })
                SystemThemeWatcher.UnWatch(_window);
            ApplicationThemeManager.Apply(_mode == ThemeMode.Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }
    }

    private static ThemeMode Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<ThemeRecord>(File.ReadAllText(ConfigPath)) is { } record
                    && Enum.TryParse<ThemeMode>(record.Mode, out var mode)
                    ? mode
                    : ThemeMode.System;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return ThemeMode.System;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new ThemeRecord(_mode.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ThemeRecord(string Mode);
}