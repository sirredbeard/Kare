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

[JsonSerializable(typeof(GenieXModelList))]
internal sealed partial class GenieXJsonContext : JsonSerializerContext;
