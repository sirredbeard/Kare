using System.Text.Json.Serialization;

namespace Kare.Service.Api;

/// <summary>
/// Compile time JSON metadata for the OpenAI compatible surface. Kare sets
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> so Native AOT stays viable,
/// which means every serialized type has to be listed here.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ModelListResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public sealed partial class OpenAiJsonContext : JsonSerializerContext;
