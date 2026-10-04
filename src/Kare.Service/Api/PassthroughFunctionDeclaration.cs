using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kare.Service.Api;

/// <summary>
/// A tool Kare declares to the model but never invokes.
/// Kare is a gateway. The caller, normally Copilot CLI, owns the tools, asks the user for
/// permission, and runs them. Kare's job is to pass the declaration down and pass the
/// requested call back up unchanged. Executing a caller's tool inside the gateway would
/// move a permission decision off the machine that made it, which is not acceptable.
/// </summary>
internal sealed class PassthroughFunctionDeclaration : AIFunctionDeclaration
{
    private static readonly JsonElement EmptySchema =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;

    public PassthroughFunctionDeclaration(string name, string? description, JsonElement? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _description = description ?? string.Empty;
        _schema = parameters?.ValueKind is JsonValueKind.Object
            ? parameters.Value.Clone()
            : EmptySchema;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _schema;

    /// <summary>
    /// Caller tools are authoritative for this gateway request. This metadata tells the
    /// Copilot SDK that a caller-provided name such as <c>bash</c> intentionally replaces
    /// the SDK's built-in tool with the same name.
    /// </summary>
    public override AdditionalPropertiesDictionary AdditionalProperties => new()
    {
        ["is_override"] = true,
        ["overridesBuiltInTool"] = true,
    };
}
