using System.Text.Json.Serialization;

namespace Kare.Service.Dashboard;

/// <summary>Section a pasted intake value was submitted to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntakeKind>))]
public enum IntakeKind
{
    Source,
    Skill,
    Mcp,
}

/// <summary>Bounded intake lifecycle status. Kare validates and applies deterministically;
/// only the interpretation step may consult a model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IntakeStatus>))]
public enum IntakeStatus
{
    Resolving,
    Fetching,
    Review,
    Indexing,
    Active,
    Stale,
    Failed,
    Disabled,
}

/// <summary>Raw paste submitted to one intake field.</summary>
public sealed record SubmitIntakeRequest(string Input);

/// <summary>
/// Normalized proposal produced by resolving a pasted authoritative-source, skill, or MCP
/// value. The model may help interpret ambiguous input; Kare alone decides validation,
/// approval requirements, and whether a record is ever written to a registry.
/// </summary>
public sealed record IntakeProposal(
    string Id,
    IntakeKind Kind,
    string RawInput,
    IntakeStatus Status,
    string? CanonicalName,
    string? CanonicalSource,
    string? InferredScope,
    string? TrustBasis,
    string? RefreshPolicy,
    bool RequiresAuth,
    string? RejectedExpansion,
    bool RequiresApproval,
    bool UsedLocalModel,
    bool UsedCloud,
    bool IsBillable,
    string? ResolvedTarget,
    string? ResolvedDescription,
    string? ResolvedRecordId,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Persisted bounded intake proposal list.</summary>
public sealed record IntakeState(List<IntakeProposal> Proposals);

/// <summary>Shared helpers for keeping credential-bearing detail out of dashboard output.</summary>
public static class EndpointRedaction
{
    /// <summary>Returns the endpoint without its query string or fragment.</summary>
    public static string Redact(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.GetLeftPart(UriPartial.Path);
    }

    /// <summary>Returns the endpoint without its query string or fragment when the value parses
    /// as an absolute URI; otherwise returns the original value unchanged.</summary>
    public static string RedactIfUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? Redact(uri) : value;
    }
}
