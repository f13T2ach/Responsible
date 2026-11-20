using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Responsible.ViewModel;

namespace Responsible.Controls;

public partial class ModelsDropMenu : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(ModelsDropMenu),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(ModelsDropMenu),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

    public static readonly DependencyProperty PlacementProperty = DependencyProperty.Register(
        nameof(Placement), typeof(PlacementMode), typeof(ModelsDropMenu),
        new PropertyMetadata(PlacementMode.Top));

    private ListCollectionView? _view;

    public ModelsDropMenu()
    {
        InitializeComponent();
        SearchBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnSearchTextChanged));
        SearchBox.PreviewKeyDown += OnSearchPreviewKeyDown;
        OptionsList.PreviewMouseLeftButtonUp += OnListMouseUp;
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public PlacementMode Placement
    {
        get => (PlacementMode)GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var menu = (ModelsDropMenu)d;
        var items = e.NewValue as IEnumerable;
        var list = items as IList ?? items?.Cast<object>().ToList();

        menu._view = list == null ? null : new ListCollectionView(list);
        if (menu._view != null)
        {
            menu._view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelOptionViewModel.ProviderName)));
            menu._view.Filter = o => menu.MatchesSearch((ModelOptionViewModel)o);
        }

        menu.OptionsList.ItemsSource = menu._view;
        menu.SyncSelectionToItems();
        menu.RefreshEmptyState();
    }

    private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var menu = (ModelsDropMenu)d;
        menu.ApplySelectionFlags(e.NewValue);
        if (!ReferenceEquals(menu.OptionsList.SelectedItem, e.NewValue))
            menu.OptionsList.SelectedItem = e.NewValue;
    }

    private void SyncSelectionToItems()
    {
        var options = ItemsSource?.Cast<ModelOptionViewModel>().ToList() ?? [];
        var current = SelectedItem as ModelOptionViewModel;
        var next = current != null && options.Contains(current)
            ? current
            : options.FirstOrDefault(o => o.DisplayName == current?.DisplayName) ?? options.FirstOrDefault();

        SelectedItem = next;
        OptionsList.SelectedItem = next;
        ApplySelectionFlags(next);
    }

    private void ApplySelectionFlags(object? selected)
    {
        if (ItemsSource == null)
            return;
        foreach (var option in ItemsSource.Cast<ModelOptionViewModel>())
            option.IsSelected = ReferenceEquals(option, selected);
    }

    private bool MatchesSearch(ModelOptionViewModel option)
    {
        var text = SearchBox.Text;
        return string.IsNullOrEmpty(text)
            || option.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || option.ProviderName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        OptionsList.SelectedItem = _view != null && SelectedItem is { } selected && _view.Contains(selected)
            ? selected
            : null;
        RefreshEmptyState();
    }

    private void RefreshEmptyState() =>
        NoMatches.Visibility = _view is { IsEmpty: true } ? Visibility.Visible : Visibility.Collapsed;

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            MenuPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    // 仅鼠标点按才提交选择：过滤/重建引起的列表选中变化不得改写 SelectedItem
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!MenuPopup.IsOpen || e.OriginalSource is not DependencyObject node)
            return;
        if (FindAncestor<ListBoxItem>(node)?.DataContext is ModelOptionViewModel option)
        {
            SelectedItem = option;
            MenuPopup.IsOpen = false;
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
        AnimateChevron(180);
        Dispatcher.BeginInvoke(() => SearchBox.FocusInput());
    }

    private void OnPopupClosed(object sender, EventArgs e) => AnimateChevron(0);

    private void AnimateChevron(double angle) =>
        Chevron.RenderTransform.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(angle, TimeSpan.FromMilliseconds(150)));
}