using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kare.Service.Api;

/// <summary>
/// Reads the OpenAI message content union. The field is either a bare string or an array
/// of typed parts, and Copilot CLI sends the array form. Kare flattens text parts and
/// rejects part types it cannot serve rather than quietly dropping them, because silently
/// discarding an image or audio part would send the model a prompt the caller did not write.
/// </summary>
public sealed class ChatContentConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.StartArray:
                return ReadParts(ref reader);

            default:
                throw new JsonException(
                    $"Message content must be a string, an array of content parts, or null. Found {reader.TokenType}.");
        }
    }

    private static string ReadParts(ref Utf8JsonReader reader)
    {
        var builder = new StringBuilder();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return builder.ToString();
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Content parts must be objects. Found {reader.TokenType}.");
            }

            string? partType = null;
            string? partText = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Malformed content part.");
                }

                var property = reader.GetString();
                reader.Read();

                switch (property)
                {
                    case "type":
                        partType = reader.GetString();
                        break;
                    case "text":
                        partText = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (!string.Equals(partType, "text", StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Kare serves text content parts only. Content part type '{partType}' is not supported.");
            }

            if (!string.IsNullOrEmpty(partText))
            {
                builder.Append(partText);
            }
        }

        throw new JsonException("Unterminated content part array.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Reads the OpenAI <c>stop</c> union, which is either a single string or an array of them.
/// </summary>
public sealed class StringOrStringArrayConverter : JsonConverter<List<string>?>
{
    /// <inheritdoc />
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var single = reader.GetString();
                return single is null ? null : [single];

            case JsonTokenType.StartArray:
                var values = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("Stop sequences must be strings.");
                    }

                    var value = reader.GetString();
                    if (value is not null)
                    {
                        values.Add(value);
                    }
                }

                return values;

            default:
                throw new JsonException($"Stop must be a string or an array of strings. Found {reader.TokenType}.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
