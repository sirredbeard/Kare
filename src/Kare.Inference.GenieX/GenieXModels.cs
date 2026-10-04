using System.Text.Json.Serialization;

namespace Kare.Inference.GenieX;

internal sealed class GenieXModelList
{
    [JsonPropertyName("data")]
    public List<GenieXModel> Data { get; set; } = [];
}

internal sealed class GenieXModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed class GenieXReadinessRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<GenieXReadinessMessage> Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 1;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

internal sealed class GenieXReadinessMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "Reply with one word.";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed class GenieXCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<GenieXCompletionMessage> Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("enable_think")]
    public bool EnableThink { get; set; }
}

internal sealed class GenieXCompletionMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed class GenieXCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<GenieXCompletionChoice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public GenieXCompletionUsage? Usage { get; set; }
}

internal sealed class GenieXCompletionChoice
{
    [JsonPropertyName("message")]
    public GenieXCompletionMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class GenieXCompletionUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; set; }
}

[JsonSerializable(typeof(GenieXModelList))]
[JsonSerializable(typeof(GenieXReadinessRequest))]
[JsonSerializable(typeof(GenieXCompletionRequest))]
[JsonSerializable(typeof(GenieXCompletionResponse))]
internal sealed partial class GenieXJsonContext : JsonSerializerContext;
