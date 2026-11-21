using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Responsible.ViewModel;

/// <summary>输入框下方审批等级的一项：值、展示文案与图标全部来自 DSH 权限目录。</summary>
public partial class PermissionOptionViewModel : ObservableObject
{
    public required string Value { get; init; }

    public required string Label { get; init; }

    public string? Description { get; init; }

    public string? Badge { get; init; }

    /// <summary>完全权限是风险项：标题、图标与说明按危险色渲染。</summary>
    public bool IsDangerous { get; init; }

    /// <summary>合成项“RVM 隔离保护”：按安全色渲染，选中即开启当前工作区的 RVM 沙箱。</summary>
    public bool IsRvmProtection { get; init; }

    public SymbolRegular Icon { get; init; }

    public bool HasIcon { get; init; }

    [ObservableProperty]
    private bool _isCurrent;
}
