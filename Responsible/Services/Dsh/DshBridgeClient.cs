using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Responsible.Services;

internal sealed record BridgePluginRow(
    string? EntryId,
    string? ModuleName,
    string? Title,
    string? Description,
    bool Enabled,
    string? FiberPhase,
    string? Diagnostic,
    string? ReadOnlyReason,
    string? PatchId);

internal sealed record BridgeEnvelope(bool Ok, BridgePluginRow[]? Data, BridgeError? Error);

internal sealed record BridgeError(string? Code, string? Message);

// 宿主插件在 DSH 进程内开命名管道；插件页经此读取实时插件清单，避免改内核的 JSON-RPC 协议。
internal static class DshBridgeClient
{
    private const string PipePrefix = @"\\.\pipe\";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<BridgePluginRow>> ListAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ListOnceAsync(bridge, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                last = ex;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"连接插件桥接超时：{last?.Message}");
    }

    private static async Task<IReadOnlyList<BridgePluginRow>> ListOnceAsync(
        DshBridgeInfo bridge, CancellationToken cancellationToken)
    {
        var name = bridge.PipeName.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)
            ? bridge.PipeName[PipePrefix.Length..]
            : bridge.PipeName;

        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var request = JsonSerializer.Serialize(new { id = 1, token = bridge.Token, op = "list" });
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);

        var line = await reader.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(line))
            throw new IOException("插件桥接没有返回数据");

        var envelope = JsonSerializer.Deserialize<BridgeEnvelope>(line, JsonOptions)
                       ?? throw new IOException("插件桥接返回了无效 JSON");
        if (!envelope.Ok)
            throw new IOException(envelope.Error?.Message ?? "插件桥接返回失败");
        return envelope.Data ?? [];
    }
}