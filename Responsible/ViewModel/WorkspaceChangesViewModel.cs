using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Responsible.Localization;
using Responsible.Services;

namespace Responsible.ViewModel;

/// <summary>本轮改动里的一个文件：状态（A/M/D）、行数，以及解析好的 diff 行。</summary>
public sealed class ChangeFileItem
{
    public required string Path { get; init; }
    public required string Display { get; init; }
    public required string Status { get; init; }
    public int Added { get; init; }
    public int Deleted { get; init; }
    public bool Binary { get; init; }
    public string? Diff { get; init; }

    public string Counts => Binary ? Loc.T("L.Changes.Binary") : $"+{Added} −{Deleted}";
}

/// <summary>diff 里的一行：hunk 头，或带左右行号的内容行。</summary>
public sealed class DiffLine
{
    public required string Kind { get; init; }      // hunk / add / del / context
    public string Old { get; init; } = string.Empty;
    public string New { get; init; } = string.Empty;
    public string Sign { get; init; } = string.Empty;
    public required string Text { get; init; }
    public bool IsHunk => Kind == "hunk";
}

/// <summary>
/// 右侧改动面板的数据：页签是这一轮改过的文件，正文是该文件按 hunk 解析后的 diff。
/// 结构与 DSH 的 review 视图一致（hunk 头、双行号、± 符号）。
/// </summary>
public partial class WorkspaceChangesViewModel : ObservableObject
{
    public ObservableCollection<ChangeFileItem> Files { get; } = [];
    public ObservableCollection<DiffLine> Lines { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private ChangeFileItem? _selected;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _emptyHint = string.Empty;

    [ObservableProperty]
    private bool _hasLines;

    // 文件选择器（DSH 的 review tab 是一个下拉，不是一排页签）
    [ObservableProperty]
    private bool _isFileMenuOpen;

    [ObservableProperty]
    private int _turn;

    public string TabTitle => Loc.F("L.Changes.Tab", Turn);

    [RelayCommand]
    private void ToggleFileMenu() => IsFileMenuOpen = !IsFileMenuOpen;

    [RelayCommand]
    private void PickFile(ChangeFileItem? file)
    {
        if (file is null) return;
        Selected = file;
        IsFileMenuOpen = false;
    }

    public void Load(ChatChanges changes)
    {
        Files.Clear();
        Lines.Clear();
        Selected = null;

        foreach (var file in changes.Files)
        {
            changes.Diffs.TryGetValue(file.Path, out var diff);
            Files.Add(new ChangeFileItem
            {
                Path = file.Path,
                Display = file.Display,
                Status = StatusOf(diff),
                Added = file.Added,
                Deleted = file.Deleted,
                Binary = file.Binary,
                Diff = diff,
            });
        }

        Turn = changes.Turn;
        OnPropertyChanged(nameof(TabTitle));
        Summary = Loc.F("L.Changes.Summary", changes.Files.Count, changes.Added, changes.Deleted);
        Selected = Files.FirstOrDefault();
        if (Files.Count > 0) IsOpen = true;
    }

    private static string StatusOf(string? diff)
    {
        if (diff is null) return "M";
        if (diff.Contains("new file mode", StringComparison.Ordinal)) return "A";
        if (diff.Contains("deleted file mode", StringComparison.Ordinal)) return "D";
        return "M";
    }

    partial void OnSelectedChanged(ChangeFileItem? value)
    {
        Lines.Clear();
        if (value?.Diff is not { Length: > 0 } diff)
        {
            HasLines = false;
            EmptyHint = value?.Binary == true
                ? Loc.T("L.Changes.BinaryHint")
                : Loc.T("L.Changes.NoTextDiff");
            return;
        }

        foreach (var line in Parse(diff)) Lines.Add(line);
        HasLines = Lines.Count > 0;
        EmptyHint = HasLines ? string.Empty : Loc.T("L.Changes.NoTextDiff");
    }

    // unified diff → 带行号的行；行号从 "@@ -a,b +c,d @@" 推
    private static IEnumerable<DiffLine> Parse(string diff)
    {
        var oldNo = 0;
        var newNo = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
                oldNo = match.Success ? int.Parse(match.Groups[1].Value) : 0;
                newNo = match.Success ? int.Parse(match.Groups[2].Value) : 0;
                inHunk = true;
                yield return new DiffLine { Kind = "hunk", Text = line };
                continue;
            }
            if (!inHunk) continue;
            if (line.StartsWith('\\')) continue;
            if (line.StartsWith('+'))
            {
                yield return new DiffLine
                {
                    Kind = "add",
                    New = (newNo++).ToString(),
                    Sign = "+",
                    Text = line[1..],
                };
            }
            else if (line.StartsWith('-'))
            {
                yield return new DiffLine
                {
                    Kind = "del",
                    Old = (oldNo++).ToString(),
                    Sign = "−",
                    Text = line[1..],
                };
            }
            else
            {
                var text = line.StartsWith(' ') ? line[1..] : line;
                yield return new DiffLine
                {
                    Kind = "context",
                    Old = (oldNo++).ToString(),
                    New = (newNo++).ToString(),
                    Text = text,
                };
            }
        }
    }
}
