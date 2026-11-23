using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Responsible.Infrastructure;

// RWebView/src/bridge.ts 的 C# 镜像：Web → C# 走 postMessage(JSON)，C# → Web 走 rwebReceive(双重 JSON 编码)
public sealed record WebPalette(
    string Bg,
    string Surface,
    string Surface2,
    string Border,
    string Text,
    string Text2,
    string Text3,
    string Accent,
    string Success,
    string Caution,
    string Critical,
    string CodeBg);

public sealed record WireUsageRoute(string Provider, string Model);

public sealed record WireUsage(
    long UncachedInputTokens,
    long OutputTokens,
    long TotalTokens,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    long? ReasoningTokens = null,
    IReadOnlyList<WireUsageRoute>? Routes = null);

public sealed record WireTool(
    string CallId,
    string Name,
    string Variant,
    string Summary,
    string State,
    long Ts,
    string? Output = null,
    string? Input = null,
    bool? IsError = null);

public sealed record WirePart(string Kind, string Text, WireTool? Tool = null);

public sealed record WireMessage(
    string Id,
    string Role,
    string Text,
    string State,
    long Ts,
    string? Model = null,
    string? Reasoning = null,
    long? ReasoningMs = null,
    WireUsage? Usage = null,
    long? BranchSeq = null,
    bool? BranchUnavailable = null,
    IReadOnlyList<WireTool>? Tools = null,
    IReadOnlyList<WirePart>? Parts = null);

public sealed record WireStats(
    int Rounds,
    int Steps,
    int TokensPerSecond,
    long TotalTokens,
    double CacheHitRate,
    double ContextPercent);

public sealed record WireChangedFile(
    string Path,
    string Display,
    int Added,
    int Deleted,
    bool? Binary = null,
    bool? Oversized = null);

public sealed record WireChanges(
    int Turn,
    string Cwd,
    int Total,
    int Added,
    int Deleted,
    IReadOnlyList<WireChangedFile> Files,
    IReadOnlyDictionary<string, string>? Diffs = null);

public sealed record HostInit(
    int Protocol,
    string Theme,
    WebPalette Colors,
    IReadOnlyList<WireMessage> Messages,
    WireStats Stats,
    IReadOnlyList<WireChanges> Changes,
    bool Loading,
    string Locale)
{
    public string Type => "init";
}

public sealed record HostTheme(string Theme, WebPalette Colors)
{
    public string Type => "theme";
}

public sealed record HostLocale(string Locale)
{
    public string Type => "locale";
}

public sealed record HostHistoryReset(IReadOnlyList<WireMessage> Messages)
{
    public string Type => "history.reset";
}

public sealed record HostHistoryLoading(bool Loading)
{
    public string Type => "history.loading";
}

public sealed record HostMessageAppend(WireMessage Message)
{
    public string Type => "message.append";
}

public sealed record HostMessageUpdate(WireMessage Message)
{
    public string Type => "message.update";
}

public sealed record HostMessageDelta(string Id, string Text)
{
    public string Type => "message.delta";
}

public sealed record HostReasoningDelta(string Id, string Text)
{
    public string Type => "reasoning.delta";
}

public sealed record HostChangesAppend(WireChanges Changes)
{
    public string Type => "changes.append";
}

/// <summary>一条待决定的审批（前端据此渲染卡片）。</summary>
public sealed record WireApproval(
    string Id,
    string ToolName,
    string? Reason = null,
    string? CallId = null,
    bool? Dangerous = null);

public sealed record WireFileEntry(string Name, bool? Dir = null, long? Size = null);

public sealed record HostWorkspaceFiles(IReadOnlyList<WireFileEntry> Entries)
{
    public string Type => "workspace.files";
}

public sealed record HostApprovalRequest(IReadOnlyList<WireApproval> Approvals)
{
    public string Type => "approval.request";
}

public sealed record HostChangesShow(WireChanges Changes, string? Path = null)
{
    public string Type => "changes.show";
}

// 历史重放后整批替换改动卡片（重启应用时把它们放回去）
public sealed record HostChangesReset(IReadOnlyList<WireChanges> Changes)
{
    public string Type => "changes.reset";
}

public sealed record HostToast(string Text)
{
    public string Type => "toast";
}

public sealed record HostMessageRestart(string Id)
{
    public string Type => "message.restart";
}

public sealed record HostMessageDone(string Id, string State)
{
    public string Type => "message.done";
}

public sealed record HostStats(WireStats Stats)
{
    public string Type => "stats";
}

public sealed record HostAgentState(string State)
{
    public string Type => "agent.state";
}

/// <summary>对话区请求打开某一轮的改动 diff；Path 为空表示看这一轮的第一个文件。</summary>
public sealed record DiffRequest(int Turn, string? Path, int? Index = null);

public sealed record WebMessage(string Type, int? Protocol = null, string? Text = null, string? Id = null,
    string? Href = null, int? Turn = null, string? Path = null, int? Index = null);

public static class WebBridge
{
    public const int ProtocolVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ScriptFor(object message) =>
        $"window.rwebReceive({JsonSerializer.Serialize(JsonSerializer.Serialize(message, Json), Json)})";

    public static WebMessage? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WebMessage>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 调色板单一来源：App 资源字典的主题刷；半透明刷按内容区底色合成后再下发
    public static WebPalette ResolvePalette()
    {
        var bg = Resolve("ApplicationBackgroundBrush", Colors.Black);
        var dark = Luminance(bg) < 0.5;
        return new WebPalette(
            Bg: Hex(bg),
            Surface: Hex(Resolve("CardBackgroundFillColorDefaultBrush", bg)),
            Surface2: Hex(Resolve("ControlFillColorSecondaryBrush", bg)),
            Border: Hex(Resolve("ControlStrokeColorSecondaryBrush", bg)),
            Text: Hex(Resolve("TextFillColorPrimaryBrush", Colors.White)),
            Text2: Hex(Resolve("TextFillColorSecondaryBrush", Colors.White)),
            Text3: Hex(Resolve("TextFillColorTertiaryBrush", Colors.Gray)),
            Accent: Hex(Resolve("AccentTextFillColorPrimaryBrush", Colors.CornflowerBlue)),
            Success: Hex(Resolve("SystemFillColorSuccessBrush", Colors.Green)),
            Caution: Hex(Resolve("SystemFillColorCautionBrush", Colors.Yellow)),
            Critical: Hex(Resolve("SystemFillColorCriticalBrush", Colors.Red)),
            CodeBg: Hex(dark ? Scale(bg, 0.78) : Mix(bg, Colors.White, 0.55)));
    }

    public static string ThemeOf(WebPalette palette) =>
        Luminance(ParseHex(palette.Bg)) < 0.5 ? "dark" : "light";

    public static Color ParseHex(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static Color Resolve(string key, Color over)
    {
        if (Application.Current?.TryFindResource(key) is not SolidColorBrush brush)
            return over;
        var c = brush.Color;
        var a = brush.Opacity * (c.A / 255.0);
        return Color.FromRgb(
            (byte)(c.R * a + over.R * (1 - a)),
            (byte)(c.G * a + over.G * (1 - a)),
            (byte)(c.B * a + over.B * (1 - a)));
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Color Scale(Color c, double k) =>
        Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
}
