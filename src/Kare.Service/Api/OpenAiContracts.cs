using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kare.Service.Api;

// These types are the OpenAI chat completions wire contract, not Kare's domain model.
// They exist only so Copilot CLI BYOK and other OpenAI compatible clients can talk to
// Kare. Keep them boring, keep them flat, and keep the translation in OpenAiTranslator.

/// <summary>An OpenAI compatible chat completion request.</summary>
public sealed class ChatCompletionRequest
{
    /// <summary>Requested model identifier. Kare serves whatever it has configured.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>The conversation so far.</summary>
    [JsonPropertyName("messages")]
    public List<ChatCompletionRequestMessage> Messages { get; set; } = [];

    /// <summary>True to stream the response as server sent events.</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>Sampling temperature.</summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>Nucleus sampling cutoff.</summary>
    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    /// <summary>Legacy output token bound.</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>Current output token bound. Takes precedence over <see cref="MaxTokens"/>.</summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    /// <summary>Stop sequences.</summary>
    [JsonPropertyName("stop")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string>? Stop { get; set; }

    /// <summary>Random seed, when the backend honours one.</summary>
    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    /// <summary>Tools the caller is willing to execute.</summary>
    [JsonPropertyName("tools")]
    public List<ChatCompletionTool>? Tools { get; set; }

    /// <summary>Tool selection mode: <c>auto</c>, <c>none</c>, <c>required</c>, or a named function.</summary>
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    /// <summary>Whether more than one tool call may be returned per turn.</summary>
    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }
}

/// <summary>One message in an OpenAI compatible request.</summary>
public sealed class ChatCompletionRequestMessage
{
    /// <summary>One of <c>system</c>, <c>developer</c>, <c>user</c>, <c>assistant</c>, or <c>tool</c>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>
    /// Message text. OpenAI allows either a bare string or an array of content parts,
    /// and Copilot CLI sends the array form, so both are accepted and flattened to text.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(ChatContentConverter))]
    public string? Content { get; set; }

    /// <summary>Tool calls requested by a previous assistant turn.</summary>
    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }

    /// <summary>The call this message is the result of, when the role is <c>tool</c>.</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>Optional participant name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>A tool the caller offers to the model.</summary>
public sealed class ChatCompletionTool
{
    /// <summary>Always <c>function</c> today.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>The function declaration.</summary>
    [JsonPropertyName("function")]
    public ChatCompletionFunction? Function { get; set; }
}

/// <summary>A function declaration.</summary>
public sealed class ChatCompletionFunction
{
    /// <summary>Function name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>What the function does.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>JSON Schema for the parameters.</summary>
    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

/// <summary>A tool call the model asked for.</summary>
public sealed class ChatCompletionToolCall
{
    /// <summary>Call identifier, echoed back on the matching tool result message.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Position in the tool call list. Required by streaming clients.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    /// <summary>Always <c>function</c> today.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>The function and its serialized arguments.</summary>
    [JsonPropertyName("function")]
    public ChatCompletionToolCallFunction? Function { get; set; }
}

/// <summary>The function part of a tool call.</summary>
public sealed class ChatCompletionToolCallFunction
{
    /// <summary>Function name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Arguments as a JSON string, which is what the OpenAI contract specifies.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

/// <summary>A non-streamed chat completion response.</summary>
public sealed class ChatCompletionResponse
{
    /// <summary>Response identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Always <c>chat.completion</c>.</summary>
    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created")]
    public long Created { get; set; }

    /// <summary>The model that served the request.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Generated choices. Kare returns exactly one.</summary>
    [JsonPropertyName("choices")]
    public List<ChatCompletionChoice> Choices { get; set; } = [];

    /// <summary>Token accounting, when the backend reports it.</summary>
    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; set; }

    /// <summary>
    /// Kare's own route record. Not part of the OpenAI contract. It is included so a
    /// caller can always see where an answer actually came from instead of guessing.
    /// </summary>
    [JsonPropertyName("kare_route")]
    public KareRouteEnvelope? KareRoute { get; set; }
}

/// <summary>One generated choice.</summary>
public sealed class ChatCompletionChoice
{
    /// <summary>Choice index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>The generated message.</summary>
    [JsonPropertyName("message")]
    public ChatCompletionResponseMessage? Message { get; set; }

    /// <summary>Why generation stopped.</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>The assistant message in a response.</summary>
public sealed class ChatCompletionResponseMessage
{
    /// <summary>Always <c>assistant</c>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    /// <summary>Generated text, or null when the turn is only tool calls.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Tool calls the model requested.</summary>
    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }
}

/// <summary>Token accounting.</summary>
public sealed class ChatCompletionUsage
{
    /// <summary>Prompt tokens.</summary>
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    /// <summary>Generated tokens.</summary>
    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    /// <summary>Sum of the two.</summary>
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }
}

/// <summary>One streamed chunk.</summary>
public sealed class ChatCompletionChunk
{
    /// <summary>Response identifier, stable across the whole stream.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Always <c>chat.completion.chunk</c>.</summary>
    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk";

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created")]
    public long Created { get; set; }

    /// <summary>The model that served the request.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Chunk choices. Kare returns exactly one.</summary>
    [JsonPropertyName("choices")]
    public List<ChatCompletionChunkChoice> Choices { get; set; } = [];

    /// <summary>Token accounting, sent on the final chunk when available.</summary>
    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; set; }

    /// <summary>Kare's route record, sent on the final chunk.</summary>
    [JsonPropertyName("kare_route")]
    public KareRouteEnvelope? KareRoute { get; set; }
}

/// <summary>One streamed choice.</summary>
public sealed class ChatCompletionChunkChoice
{
    /// <summary>Choice index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>The incremental update.</summary>
    [JsonPropertyName("delta")]
    public ChatCompletionDelta? Delta { get; set; }

    /// <summary>Why generation stopped, on the last chunk only.</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>An incremental streamed update.</summary>
public sealed class ChatCompletionDelta
{
    /// <summary>Set on the first chunk only.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>Incremental text.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Incremental tool calls.</summary>
    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }
}

/// <summary>
/// Kare's route disclosure. Every response carries one so a local answer and a billable
/// cloud answer are never indistinguishable to the caller.
/// </summary>
public sealed class KareRouteEnvelope
{
    /// <summary>The route that served the request.</summary>
    [JsonPropertyName("route")]
    public string Route { get; set; } = string.Empty;

    /// <summary>The execution path, such as the CPU or NPU backend.</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = string.Empty;

    /// <summary>Why this route was chosen. Never contains prompt text.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>True when the route spent Copilot or Azure credit.</summary>
    [JsonPropertyName("billable")]
    public bool Billable { get; set; }

    /// <summary>The route this one replaced, when it was a fallback.</summary>
    [JsonPropertyName("fell_back_from")]
    public string? FellBackFrom { get; set; }
}

/// <summary>The <c>/v1/models</c> list response.</summary>
public sealed class ModelListResponse
{
    /// <summary>Always <c>list</c>.</summary>
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    /// <summary>Available models.</summary>
    [JsonPropertyName("data")]
    public List<ModelDescription> Data { get; set; } = [];
}

/// <summary>One advertised model.</summary>
public sealed class ModelDescription
{
    /// <summary>Model identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Always <c>model</c>.</summary>
    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("created")]
    public long Created { get; set; }

    /// <summary>Who serves it.</summary>
    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "kare";
}

/// <summary>An OpenAI shaped error response.</summary>
public sealed class ErrorResponse
{
    /// <summary>The error body.</summary>
    [JsonPropertyName("error")]
    public ErrorBody Error { get; set; } = new();
}

/// <summary>Error detail.</summary>
public sealed class ErrorBody
{
    /// <summary>Human readable message. Never contains prompt text or secrets.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Coarse error class.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "invalid_request_error";

    /// <summary>Stable machine readable code.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
