using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kare.Service.Api;

/// <summary>
/// Converts tool call arguments between the OpenAI wire form, which is a JSON string, and
/// the <see cref="FunctionCallContent.Arguments"/> dictionary.
///
/// This is written by hand against <see cref="Utf8JsonWriter"/> instead of calling a generic
/// serializer because Kare runs with reflection based serialization disabled for Native AOT.
/// Anything it cannot represent faithfully throws, because a mangled tool argument is worse
/// than a failed request.
/// </summary>
internal static class ToolArgumentJson
{
    /// <summary>Parses an OpenAI arguments string into an argument dictionary.</summary>
    public static Dictionary<string, object?> Parse(string? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        using var document = JsonDocument.Parse(arguments);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool call arguments must be a JSON object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>Serializes an argument dictionary into the OpenAI arguments string.</summary>
    public static string Serialize(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, key, value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonDocument document:
                document.RootElement.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            default:
                throw new NotSupportedException(
                    $"Tool argument '{key}' has unsupported type {value.GetType().Name}. " +
                    "Kare serializes tool arguments without reflection, so only JSON primitives " +
                    "and JsonElement values can be forwarded.");
        }
    }
}
