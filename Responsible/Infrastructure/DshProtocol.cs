using System.Text.Json;
using System.Text.Json.Serialization;

namespace Responsible.Infrastructure;

// dsh-sdk-protocol：每行一条 JSON-RPC 2.0；id+method=请求，仅 id=响应，仅 method=通知
public static class DshProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Request(int id, string method, object parameters) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }, Json);

    public static string Response(int id, object result) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Json);

    public static JsonDocument? Parse(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}