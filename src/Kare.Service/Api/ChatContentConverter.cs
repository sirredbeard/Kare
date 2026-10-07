using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Kare.Service.Api;

/// <summary>Ordered text and inline image content from one OpenAI message.</summary>
public sealed class ChatMessageContent
{
    internal ChatMessageContent(IReadOnlyList<AIContent> contents)
    {
        Contents = contents;
    }

    internal IReadOnlyList<AIContent> Contents { get; }

    /// <summary>Concatenated text content.</summary>
    public string Text => string.Concat(
        Contents.OfType<TextContent>().Select(static content => content.Text));
}

/// <summary>
/// Reads the OpenAI message content union. Copilot CLI sends text and inline image parts.
/// Remote image URLs remain unsupported so Kare never fetches caller-controlled locations.
/// </summary>
public sealed class ChatContentConverter : JsonConverter<ChatMessageContent?>
{
    private const int MaxInlineImageBytes = 20 * 1024 * 1024;

    /// <inheritdoc />
    public override ChatMessageContent? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new ChatMessageContent(
                    [new TextContent(reader.GetString() ?? string.Empty)]);

            case JsonTokenType.StartArray:
                return ReadParts(ref reader);

            default:
                throw new JsonException(
                    $"Message content must be a string, an array of content parts, or null. Found {reader.TokenType}.");
        }
    }

    private static ChatMessageContent ReadParts(ref Utf8JsonReader reader)
    {
        var contents = new List<AIContent>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return new ChatMessageContent(contents);
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Content parts must be objects. Found {reader.TokenType}.");
            }

            string? partType = null;
            string? partText = null;
            string? imageUrl = null;

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
                    case "image_url":
                        imageUrl = ReadImageUrl(ref reader);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            switch (partType)
            {
                case "text":
                    if (!string.IsNullOrEmpty(partText))
                    {
                        contents.Add(new TextContent(partText));
                    }

                    break;

                case "image_url":
                    contents.Add(CreateImageContent(imageUrl));
                    break;

                default:
                    throw new JsonException(
                        $"Kare does not support content part type '{partType}'.");
            }
        }

        throw new JsonException("Unterminated content part array.");
    }

    private static string? ReadImageUrl(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("image_url must be an object.");
        }

        string? url = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Malformed image_url content.");
            }

            var property = reader.GetString();
            reader.Read();
            if (property == "url")
            {
                url = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return url;
    }

    private static DataContent CreateImageContent(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new JsonException("image_url.url is required.");
        }

        DataContent content;
        try
        {
            content = new DataContent(imageUrl, mediaType: null);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(
                "Kare accepts inline data image URLs only; remote image URLs are not supported.",
                ex);
        }

        if (!content.HasTopLevelMediaType("image"))
        {
            throw new JsonException($"Content media type '{content.MediaType}' is not an image.");
        }

        if (content.Data.Length > MaxInlineImageBytes)
        {
            throw new JsonException(
                $"Inline image content exceeds the {MaxInlineImageBytes}-byte limit.");
        }

        return content;
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        ChatMessageContent? value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var content in value.Contents)
        {
            writer.WriteStartObject();
            switch (content)
            {
                case TextContent text:
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    break;

                case DataContent data when data.HasTopLevelMediaType("image"):
                    writer.WriteString("type", "image_url");
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", data.Uri);
                    writer.WriteEndObject();
                    break;

                default:
                    throw new JsonException(
                        $"Kare cannot write content type '{content.GetType().Name}'.");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
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
