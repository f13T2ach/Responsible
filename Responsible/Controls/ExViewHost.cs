using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Responsible.ExView;
using Responsible.Messages;

namespace Responsible.Controls;

public class ExViewHost : ContentControl
{
    private const int WmNchitTest = 0x0084;
    private const int HtClient = 1;

    private HwndSource? _source;
    private FrameworkElement? _scrim;
    private FrameworkElement? _card;
    private IResponsibleExView? _current;
    private int _session;

    public ExViewHost()
    {
        Visibility = Visibility.Collapsed;
        WeakReferenceMessenger.Default.Register<OpenExViewMessage>(
            this, static (r, m) => ((ExViewHost)r).Open(m.Value));
        WeakReferenceMessenger.Default.Register<CloseExViewMessage>(
            this, static (r, _) => ((ExViewHost)r).Close());
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scrim = GetTemplateChild("Scrim") as FrameworkElement;
        _card = GetTemplateChild("Card") as FrameworkElement;
        if (_scrim != null)
            _scrim.PreviewMouseLeftButtonDown += (_, _) => Close();
    }

    private void Open(Type viewType)
    {
        if (_current != null)
            return;

        if (((App)Application.Current).Services.GetRequiredService(viewType) is not FrameworkElement view
            || view is not IResponsibleExView exView)
            return;

        _current = exView;
        Content = view;
        Visibility = Visibility.Visible;
        Opacity = 0;

        // 打开期间整窗判为客户端区，避免 TitleBar 钩子把覆盖层上的点击吞成拖拽
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(HitTestHook);

        if (_card != null)
        {
            _card.RenderTransformOrigin = new Point(0.5, 0.5);
            _card.RenderTransform = new ScaleTransform(0.97, 0.97);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            _card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        WeakReferenceMessenger.Default.Send(new ExViewOpenChangedMessage(true));
        exView.OnOpened();
        Dispatcher.BeginInvoke(() => view.Focus());
    }

    private void Close()
    {
        if (_current == null)
            return;

        var closing = _current;
        _current = null;
        _source?.RemoveHook(HitTestHook);
        _source = null;

        var session = ++_session;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            if (session != _session)
                return;
            Visibility = Visibility.Collapsed;
            Content = null;
            WeakReferenceMessenger.Default.Send(new ExViewOpenChangedMessage(false));
            closing.OnClosed();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private nint HitTestHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmNchitTest)
        {
            handled = true;
            return HtClient;
        }

        return nint.Zero;
    }
}