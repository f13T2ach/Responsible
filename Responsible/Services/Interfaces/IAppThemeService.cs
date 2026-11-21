using System.Windows;

namespace Responsible.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public interface IAppThemeService
{
    ThemeMode Mode { get; set; }

    void Attach(Window window);
}
