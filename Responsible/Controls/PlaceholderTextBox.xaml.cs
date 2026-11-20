using System.Windows;
using System.Windows.Controls;

namespace Responsible.Controls;

public partial class PlaceholderTextBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PlaceholderTextBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PlaceholderTextBox), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty InputStyleProperty = DependencyProperty.Register(
        nameof(InputStyle), typeof(Style), typeof(PlaceholderTextBox), new PropertyMetadata(null));

    public static readonly DependencyProperty PlaceholderMarginProperty = DependencyProperty.Register(
        nameof(PlaceholderMargin), typeof(Thickness), typeof(PlaceholderTextBox), new PropertyMetadata(new Thickness(10, 0, 10, 0)));

    public PlaceholderTextBox() => InitializeComponent();

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public Style? InputStyle
    {
        get => (Style?)GetValue(InputStyleProperty);
        set => SetValue(InputStyleProperty, value);
    }

    public Thickness PlaceholderMargin
    {
        get => (Thickness)GetValue(PlaceholderMarginProperty);
        set => SetValue(PlaceholderMarginProperty, value);
    }

    public void FocusInput() => Input.Focus();
}