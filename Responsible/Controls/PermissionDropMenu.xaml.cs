using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Responsible.ViewModel;
using Wpf.Ui.Controls;

namespace Responsible.Controls;

public partial class PermissionDropMenu : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(PermissionDropMenu),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CurrentLabelProperty = DependencyProperty.Register(
        nameof(CurrentLabel), typeof(string), typeof(PermissionDropMenu),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CurrentIconProperty = DependencyProperty.Register(
        nameof(CurrentIcon), typeof(SymbolRegular), typeof(PermissionDropMenu),
        new PropertyMetadata(default(SymbolRegular)));

    public static readonly DependencyProperty CurrentHasIconProperty = DependencyProperty.Register(
        nameof(CurrentHasIcon), typeof(bool), typeof(PermissionDropMenu),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CurrentDescriptionProperty = DependencyProperty.Register(
        nameof(CurrentDescription), typeof(string), typeof(PermissionDropMenu),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CurrentIsDangerousProperty = DependencyProperty.Register(
        nameof(CurrentIsDangerous), typeof(bool), typeof(PermissionDropMenu),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CurrentIsRvmProtectionProperty = DependencyProperty.Register(
        nameof(CurrentIsRvmProtection), typeof(bool), typeof(PermissionDropMenu),
        new PropertyMetadata(false));

    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand), typeof(ICommand), typeof(PermissionDropMenu),
        new PropertyMetadata(null));

    public PermissionDropMenu()
    {
        InitializeComponent();
        OptionsList.PreviewMouseLeftButtonUp += OnListMouseUp;
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string CurrentLabel
    {
        get => (string)GetValue(CurrentLabelProperty);
        set => SetValue(CurrentLabelProperty, value);
    }

    public SymbolRegular CurrentIcon
    {
        get => (SymbolRegular)GetValue(CurrentIconProperty);
        set => SetValue(CurrentIconProperty, value);
    }

    public bool CurrentHasIcon
    {
        get => (bool)GetValue(CurrentHasIconProperty);
        set => SetValue(CurrentHasIconProperty, value);
    }

    public string? CurrentDescription
    {
        get => (string?)GetValue(CurrentDescriptionProperty);
        set => SetValue(CurrentDescriptionProperty, value);
    }

    public bool CurrentIsDangerous
    {
        get => (bool)GetValue(CurrentIsDangerousProperty);
        set => SetValue(CurrentIsDangerousProperty, value);
    }

    public bool CurrentIsRvmProtection
    {
        get => (bool)GetValue(CurrentIsRvmProtectionProperty);
        set => SetValue(CurrentIsRvmProtectionProperty, value);
    }

    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    // 只有鼠标点按才提交：开合与列表重建不得触发切换
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!MenuPopup.IsOpen || e.OriginalSource is not DependencyObject node)
            return;
        if (FindAncestor<ListBoxItem>(node)?.DataContext is PermissionOptionViewModel option)
        {
            MenuPopup.IsOpen = false;
            if (SelectCommand?.CanExecute(option) == true)
                SelectCommand.Execute(option);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : class
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnTriggerClick(object sender, RoutedEventArgs e) =>
        MenuPopup.IsOpen = !MenuPopup.IsOpen;

    private void OnPopupOpened(object sender, EventArgs e)
    {
        OptionsList.SelectedItem = null;
        AnimateChevron(180);
    }

    private void OnPopupClosed(object sender, EventArgs e) => AnimateChevron(0);

    private void AnimateChevron(double angle) =>
        Chevron.RenderTransform.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(angle, TimeSpan.FromMilliseconds(150)));
}
