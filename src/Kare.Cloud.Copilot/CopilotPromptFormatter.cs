using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kare.Cloud.Copilot;

internal static class CopilotPromptFormatter
{
    public static string Format(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append('[').Append(message.Role.Value).AppendLine("]");
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        builder.AppendLine(text.Text);
                        break;

                    case FunctionCallContent call:
                        builder
                            .Append("tool_call ")
                            .Append(call.CallId)
                            .Append(' ')
                            .Append(call.Name)
                            .Append(' ')
                            .AppendLine(FormatArguments(call.Arguments));
                        break;

                    case FunctionResultContent result:
                        builder
                            .Append("tool_result ")
                            .Append(result.CallId)
                            .Append(' ')
                            .AppendLine(result.Result?.ToString() ?? string.Empty);
                        break;
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in arguments)
            {
                writer.WritePropertyName(name);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
