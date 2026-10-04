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

[JsonSerializable(typeof(GenieXModelList))]
[JsonSerializable(typeof(GenieXReadinessRequest))]
internal sealed partial class GenieXJsonContext : JsonSerializerContext;
