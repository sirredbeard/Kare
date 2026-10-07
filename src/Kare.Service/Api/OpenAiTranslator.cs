using System.Text.Json;
using Kare.Abstractions;
using Microsoft.Extensions.AI;

namespace Kare.Service.Api;

/// <summary>
/// Translates between the OpenAI chat completions wire contract and
/// <c>Microsoft.Extensions.AI</c> types. All wire knowledge lives here so the rest of Kare
/// only ever sees <see cref="ChatMessage"/>, <see cref="ChatOptions"/>, and
/// <see cref="ChatResponse"/>.
/// </summary>
internal static class OpenAiTranslator
{
    /// <summary>Converts request messages into chat messages.</summary>
    /// <exception cref="InvalidRequestException">A message is malformed or uses an unknown role.</exception>
    public static List<ChatMessage> ToChatMessages(List<ChatCompletionRequestMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new InvalidRequestException("At least one message is required.", "missing_messages");
        }

        var result = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "system":
                case "developer":
                    result.Add(new ChatMessage(
                        ChatRole.System,
                        GetTextOnlyContent(message, message.Role)));
                    break;

                case "user":
                    result.Add(new ChatMessage(
                        ChatRole.User,
                        message.Content?.Contents.Count > 0
                            ? [.. message.Content.Contents]
                            : [new TextContent(string.Empty)]));
                    break;

                case "assistant":
                    result.Add(ToAssistantMessage(message));
                    break;

                case "tool":
                    result.Add(ToToolMessage(message));
                    break;

                default:
                    throw new InvalidRequestException(
                        $"Unsupported message role '{message.Role}'.",
                        "unsupported_role");
            }
        }

        return result;
    }

    private static ChatMessage ToAssistantMessage(ChatCompletionRequestMessage message)
    {
        var contents = new List<AIContent>();

        var messageText = GetTextOnlyContent(message, "assistant");
        if (!string.IsNullOrEmpty(messageText))
        {
            contents.Add(new TextContent(messageText));
        }

        foreach (var call in message.ToolCalls ?? [])
        {
            if (string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Function?.Name))
            {
                throw new InvalidRequestException(
                    "Assistant tool calls require an id and a function name.",
                    "malformed_tool_call");
            }

            contents.Add(new FunctionCallContent(
                call.Id,
                call.Function.Name,
                ToolArgumentJson.Parse(call.Function.Arguments)));
        }

        if (contents.Count == 0)
        {
            contents.Add(new TextContent(string.Empty));
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static ChatMessage ToToolMessage(ChatCompletionRequestMessage message)
    {
        if (string.IsNullOrEmpty(message.ToolCallId))
        {
            throw new InvalidRequestException(
                "A tool message requires tool_call_id.",
                "missing_tool_call_id");
        }

        return new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(
                message.ToolCallId,
                GetTextOnlyContent(message, "tool"))]);
    }

    private static string GetTextOnlyContent(
        ChatCompletionRequestMessage message,
        string role)
    {
        if (message.Content is null)
        {
            return string.Empty;
        }

        if (message.Content.Contents.Any(static content => content is not TextContent))
        {
            throw new InvalidRequestException(
                $"{role} messages cannot contain image content.",
                "unsupported_message_content");
        }

        return message.Content.Text;
    }

    /// <summary>
    /// Converts request sampling settings into chat options. Kare does not clamp here.
    /// Bounds are applied by <c>BoundedChatClient</c> so there is exactly one place that
    /// enforces device limits.
    /// </summary>
    public static ChatOptions ToChatOptions(ChatCompletionRequest request, string modelId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new ChatOptions
        {
            ModelId = modelId,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Seed = request.Seed,
            MaxOutputTokens = request.MaxCompletionTokens ?? request.MaxTokens,
            AllowMultipleToolCalls = request.ParallelToolCalls,
        };

        if (request.Stop is { Count: > 0 })
        {
            options.StopSequences = request.Stop;
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new List<AITool>(request.Tools.Count);
            foreach (var tool in request.Tools)
            {
                if (!string.Equals(tool.Type, "function", StringComparison.Ordinal))
                {
                    throw new InvalidRequestException(
                        $"Unsupported tool type '{tool.Type}'. Kare forwards function tools only.",
                        "unsupported_tool_type");
                }

                if (tool.Function is null || string.IsNullOrWhiteSpace(tool.Function.Name))
                {
                    throw new InvalidRequestException("Each tool requires a function name.", "malformed_tool");
                }

                tools.Add(new PassthroughFunctionDeclaration(
                    tool.Function.Name,
                    tool.Function.Description,
                    tool.Function.Parameters));
            }

            options.Tools = tools;
        }

        options.ToolMode = ToToolMode(request.ToolChoice);
        return options;
    }

    private static ChatToolMode? ToToolMode(JsonElement? toolChoice)
    {
        if (toolChoice is not { } choice)
        {
            return null;
        }

        switch (choice.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;

            case JsonValueKind.String:
                return choice.GetString() switch
                {
                    "auto" => ChatToolMode.Auto,
                    "none" => ChatToolMode.None,
                    "required" or "any" => ChatToolMode.RequireAny,
                    var other => throw new InvalidRequestException(
                        $"Unsupported tool_choice '{other}'.",
                        "unsupported_tool_choice"),
                };

            case JsonValueKind.Object:
                if (choice.TryGetProperty("function", out var function) &&
                    function.TryGetProperty("name", out var name) &&
                    name.GetString() is { Length: > 0 } required)
                {
                    return ChatToolMode.RequireSpecific(required);
                }

                throw new InvalidRequestException(
                    "A tool_choice object requires function.name.",
                    "unsupported_tool_choice");

            default:
                throw new InvalidRequestException(
                    "tool_choice must be a string or an object.",
                    "unsupported_tool_choice");
        }
    }

    /// <summary>Converts a completed response into the OpenAI response shape.</summary>
    public static ChatCompletionResponse ToCompletionResponse(
        ChatResponse response,
        string responseId,
        string modelId,
        RouteDecision decision)
    {
        ArgumentNullException.ThrowIfNull(response);

        string? text = null;
        List<ChatCompletionToolCall>? toolCalls = null;

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent textContent when textContent.Text.Length > 0:
                        text = text is null ? textContent.Text : text + textContent.Text;
                        break;

                    case FunctionCallContent call:
                        toolCalls ??= [];
                        toolCalls.Add(ToToolCall(call, toolCalls.Count));
                        break;
                }
            }
        }

        return new ChatCompletionResponse
        {
            Id = responseId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId,
            Choices =
            [
                new ChatCompletionChoice
                {
                    Index = 0,
                    Message = new ChatCompletionResponseMessage
                    {
                        Content = text,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = ToFinishReason(response.FinishReason, toolCalls is { Count: > 0 }),
                },
            ],
            Usage = ToUsage(response.Usage),
            KareRoute = ToRouteEnvelope(decision),
        };
    }

    /// <summary>Converts one streamed update into a chunk, or null when it carries nothing.</summary>
    public static ChatCompletionChunk? ToChunk(
        ChatResponseUpdate update,
        string responseId,
        string modelId,
        bool isFirst,
        ref int toolCallIndex)
    {
        ArgumentNullException.ThrowIfNull(update);

        string? text = null;
        List<ChatCompletionToolCall>? toolCalls = null;
        UsageDetails? usage = null;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent textContent when textContent.Text.Length > 0:
                    text = text is null ? textContent.Text : text + textContent.Text;
                    break;

                case FunctionCallContent call:
                    toolCalls ??= [];
                    toolCalls.Add(ToToolCall(call, toolCallIndex++));
                    break;

                case UsageContent usageContent:
                    usage = usageContent.Details;
                    break;
            }
        }

        var finishReason = ToFinishReason(update.FinishReason, toolCalls is { Count: > 0 });
        var hasPayload = text is not null || toolCalls is not null || finishReason is not null || usage is not null;

        if (!hasPayload && !isFirst)
        {
            return null;
        }

        return new ChatCompletionChunk
        {
            Id = responseId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new ChatCompletionDelta
                    {
                        Role = isFirst ? "assistant" : null,
                        Content = text,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = finishReason,
                },
            ],
            Usage = ToUsage(usage),
        };
    }

    /// <summary>Builds the route disclosure attached to every response.</summary>
    public static KareRouteEnvelope ToRouteEnvelope(RouteDecision decision) => new()
    {
        Route = decision.Route.ToString(),
        Backend = decision.Backend.ToString(),
        Reason = decision.Reason,
        Billable = decision.IsBillable,
        FellBackFrom = decision.IsFallback ? decision.FellBackFrom.ToString() : null,
    };

    private static ChatCompletionToolCall ToToolCall(FunctionCallContent call, int index) => new()
    {
        Id = call.CallId,
        Index = index,
        Function = new ChatCompletionToolCallFunction
        {
            Name = call.Name,
            Arguments = ToolArgumentJson.Serialize(call.Arguments),
        },
    };

    private static ChatCompletionUsage? ToUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;

        return new ChatCompletionUsage
        {
            PromptTokens = input,
            CompletionTokens = output,
            TotalTokens = usage.TotalTokenCount ?? input + output,
        };
    }

    private static string? ToFinishReason(ChatFinishReason? reason, bool hasToolCalls)
    {
        if (reason is null)
        {
            return null;
        }

        if (reason == ChatFinishReason.Length)
        {
            return "length";
        }

        if (reason == ChatFinishReason.ContentFilter)
        {
            return "content_filter";
        }

        if (reason == ChatFinishReason.ToolCalls)
        {
            return "tool_calls";
        }

        // A model that emitted tool calls and then stopped is reported as tool_calls, because
        // that is what an OpenAI client keys on to decide whether to run the tools.
        return hasToolCalls ? "tool_calls" : "stop";
    }
}

/// <summary>
/// A request Kare refuses before any model is touched. Carries an OpenAI shaped error code.
/// </summary>
public sealed class InvalidRequestException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Operator safe description. Must not contain prompt text.</param>
    /// <param name="code">Stable machine readable code.</param>
    public InvalidRequestException(string message, string code)
        : base(message) => Code = code;

    /// <summary>Stable machine readable code.</summary>
    public string Code { get; }
}
