using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Responsible.Infrastructure.Behaviors;

/// <summary>
/// 控件级失焦行为：附加到输入控件（可经样式 Setter 批量附着）。
/// 点击控件内部显式聚焦（对抗菜单收起吞点击）；点击其他可聚焦控件不干预（保持菜单模式）；
/// 点击空白把焦点移交给最近的可聚焦祖先，无祖先时清除逻辑焦点；标题栏拖拽区点击经 Win32 钩子补齐。
/// </summary>
public static class Defocus
{
    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 2;

    public static readonly DependencyProperty OutsideClickProperty = DependencyProperty.RegisterAttached(
        "OutsideClick",
        typeof(bool),
        typeof(Defocus),
        new PropertyMetadata(false, OnOutsideClickChanged));

    private static readonly Dictionary<FrameworkElement, Subscription> Subscriptions = new();

    public static bool GetOutsideClick(DependencyObject obj) => (bool)obj.GetValue(OutsideClickProperty);

    public static void SetOutsideClick(DependencyObject obj, bool value) => obj.SetValue(OutsideClickProperty, value);

    private static void OnOutsideClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }
        }
        else
        {
            Detach(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((FrameworkElement)sender);

    private static void Attach(FrameworkElement element)
    {
        if (Subscriptions.ContainsKey(element) || Window.GetWindow(element) is not Window window)
        {
            return;
        }

        var subscription = new Subscription { Window = window };
        subscription.PreviewHandler = (_, e) => OnWindowPreviewMouseDown(element, e);
        window.PreviewMouseLeftButtonDown += subscription.PreviewHandler;

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            subscription.Source = source;
            subscription.CaptionHook = (nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
            {
                if (msg == WmNclButtonDown && wParam == (nint)HtCaption && element.IsKeyboardFocusWithin)
                {
                    element.Dispatcher.BeginInvoke(() => DefocusNow(element));
                }

                return nint.Zero;
            };
            source.AddHook(subscription.CaptionHook);
        }

        Subscriptions[element] = subscription;
    }

    private static void Detach(FrameworkElement element)
    {
        if (!Subscriptions.TryGetValue(element, out var subscription))
        {
            return;
        }

        subscription.Window.PreviewMouseLeftButtonDown -= subscription.PreviewHandler;
        if (subscription.Source != null && subscription.CaptionHook != null)
        {
            subscription.Source.RemoveHook(subscription.CaptionHook);
        }

        Subscriptions.Remove(element);
    }

    private static void OnWindowPreviewMouseDown(FrameworkElement element, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (IsWithin(source, element))
        {
            element.Focus();
            return;
        }

        if (!element.IsKeyboardFocusWithin)
        {
            return;
        }

        var sink = FindFocusableAncestor(ParentOf(element));
        var target = FindFocusableAncestor(source, sink);
        if (target != null && target != sink)
        {
            return;
        }

        DefocusNow(element);
    }

    private static void DefocusNow(FrameworkElement element)
    {
        if (FindFocusableAncestor(ParentOf(element)) is UIElement sink)
        {
            sink.Focus();
        }
        else
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(element), null);
        }
    }

    private static bool IsWithin(DependencyObject node, DependencyObject root)
    {
        DependencyObject? current = node;
        while (current != null)
        {
            if (current == root)
            {
                return true;
            }

            current = ParentOf(current);
        }

        return false;
    }

    private static DependencyObject? FindFocusableAncestor(DependencyObject? node, DependencyObject? stopAt = null)
    {
        DependencyObject? current = node;
        while (current != null && current != stopAt)
        {
            if (current is UIElement { Focusable: true })
            {
                return current;
            }

            current = ParentOf(current);
        }

        return current == stopAt ? stopAt : null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private sealed class Subscription
    {
        public Window Window { get; set; } = null!;

        public HwndSource? Source { get; set; }

        public MouseButtonEventHandler PreviewHandler { get; set; } = null!;

        public HwndSourceHook? CaptionHook { get; set; }
    }
}