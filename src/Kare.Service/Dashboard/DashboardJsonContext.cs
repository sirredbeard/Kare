using System.Text.Json.Serialization;

namespace Kare.Service.Dashboard;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardMetrics.Snapshot))]
public sealed partial class DashboardJsonContext : JsonSerializerContext;
