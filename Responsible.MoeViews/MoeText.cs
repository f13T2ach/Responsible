using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;

namespace Responsible.MoeViews;

// MoeViews 不引用宿主程序集，UI 文案统一从应用级资源字典读取
public static class MoeText
{
    public static string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static void NotifyLanguageChanged() =>
        WeakReferenceMessenger.Default.Send(new MoeLanguageChangedMessage());
}

public sealed class MoeLanguageChangedMessage;