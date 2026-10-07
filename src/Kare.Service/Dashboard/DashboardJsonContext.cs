using System.Text.Json.Serialization;

namespace Kare.Service.Dashboard;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardMetrics.Snapshot))]
[JsonSerializable(typeof(CreateAuthoritativeSourceRequest))]
[JsonSerializable(typeof(CreateDashboardSkillRequest))]
[JsonSerializable(typeof(CreateDashboardMcpServerRequest))]
[JsonSerializable(typeof(DashboardRegistryState))]
[JsonSerializable(typeof(SourceContentSnapshot))]
[JsonSerializable(typeof(RemoteSkillSnapshot))]
[JsonSerializable(typeof(McpCapabilitySnapshot))]
[JsonSerializable(typeof(McpToolSnapshot))]
public sealed partial class DashboardJsonContext : JsonSerializerContext;
