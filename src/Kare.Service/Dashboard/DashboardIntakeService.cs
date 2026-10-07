using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kare.Service.Inference;
using Kare.Service.Storage;
using Microsoft.Extensions.AI;

namespace Kare.Service.Dashboard;

/// <summary>
/// Accepts one pasted value per section (authoritative source, skill, or MCP server),
/// classifies it deterministically, optionally asks the local model to interpret
/// ambiguous input, and produces a normalized <see cref="IntakeProposal"/>. Kare alone
/// decides whether a proposal activates automatically or requires approval, and Kare
/// alone writes the resulting record through <see cref="IDashboardKnowledgeService"/>.
/// The model never writes the registry and is never given shell, browser, or MCP access.
/// </summary>
public interface IDashboardIntakeService
{
    Task<IntakeProposal> SubmitAsync(IntakeKind kind, string input, CancellationToken cancellationToken);

    Task<IntakeProposal?> ApproveAsync(string id, CancellationToken cancellationToken);

    Task<IntakeProposal?> RetryAsync(string id, CancellationToken cancellationToken);

    Task<IntakeProposal?> DisableAsync(string id, CancellationToken cancellationToken);

    Task<IntakeProposal?> RefreshAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    IReadOnlyList<IntakeProposal> GetProposals();
}

public sealed partial class DashboardIntakeService : IDashboardIntakeService
{
    private const int MaxProposals = 200;
    private const int MaxInputCharacters = 2_000;
    private const int MaxStateBytes = 2 * 1024 * 1024;
    private const int StateBackupCount = 2;
    private static readonly TimeSpan LocalModelTimeout = TimeSpan.FromSeconds(20);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, IntakeProposal> _proposals = new(StringComparer.Ordinal);
    private readonly IDashboardKnowledgeService _knowledge;
    private readonly SelectedBackendChatClient? _localModel;
    private readonly ILogger<DashboardIntakeService> _logger;
    private readonly string _statePath;
    private readonly SemaphoreSlim _persistGate = new(1, 1);

    public DashboardIntakeService(
        IDashboardKnowledgeService knowledge,
        ILogger<DashboardIntakeService> logger,
        SelectedBackendChatClient? localModel = null)
        : this(knowledge, logger, GetDefaultStatePath(), localModel)
    {
    }

    internal DashboardIntakeService(
        IDashboardKnowledgeService knowledge,
        ILogger<DashboardIntakeService> logger,
        string statePath,
        SelectedBackendChatClient? localModel = null)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Path.IsPathRooted(statePath))
        {
            throw new ArgumentException("Intake state path must be absolute.", nameof(statePath));
        }

        _knowledge = knowledge;
        _logger = logger;
        _statePath = statePath;
        _localModel = localModel;
        Load();
    }

    public IReadOnlyList<IntakeProposal> GetProposals()
    {
        lock (_sync)
        {
            return [.. _proposals.Values.OrderByDescending(static item => item.CreatedAt)];
        }
    }

    public async Task<IntakeProposal> SubmitAsync(
        IntakeKind kind,
        string input,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var trimmed = input.Trim();
        if (trimmed.Length > MaxInputCharacters)
        {
            trimmed = trimmed[..MaxInputCharacters];
        }

        var now = DateTime.UtcNow;
        var classification = kind switch
        {
            IntakeKind.Source => ClassifySource(trimmed),
            IntakeKind.Skill => ClassifySkill(trimmed),
            IntakeKind.Mcp => ClassifyMcp(trimmed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (classification.NeedsLocalModel)
        {
            classification = await InterpretWithLocalModelAsync(
                kind,
                trimmed,
                classification,
                cancellationToken).ConfigureAwait(false);
        }

        var id = Guid.NewGuid().ToString("N");
        var proposal = new IntakeProposal(
            id,
            kind,
            trimmed,
            classification.Status,
            classification.CanonicalName,
            classification.CanonicalSource,
            classification.InferredScope,
            classification.TrustBasis,
            classification.RefreshPolicy,
            classification.RequiresAuth,
            classification.RejectedExpansion,
            classification.RequiresApproval,
            classification.UsedLocalModel,
            UsedCloud: false,
            IsBillable: false,
            classification.ResolvedTarget,
            classification.ResolvedDescription,
            ResolvedRecordId: null,
            classification.Error,
            now,
            now);

        lock (_sync)
        {
            if (_proposals.Count >= MaxProposals)
            {
                var oldest = _proposals.Values
                    .Where(static item => item.Status is IntakeStatus.Disabled or IntakeStatus.Failed)
                    .OrderBy(static item => item.CreatedAt)
                    .FirstOrDefault();
                if (oldest is not null)
                {
                    _proposals.Remove(oldest.Id);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"At most {MaxProposals} intake proposals may be pending at once.");
                }
            }

            _proposals[id] = proposal;
        }

        if (!classification.RequiresApproval && classification.Status != IntakeStatus.Failed)
        {
            proposal = await ActivateAsync(proposal, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return proposal;
    }

    public async Task<IntakeProposal?> ApproveAsync(string id, CancellationToken cancellationToken)
    {
        var proposal = Get(id);
        if (proposal is null || proposal.Status is IntakeStatus.Active or IntakeStatus.Disabled)
        {
            return proposal;
        }

        var activated = await ActivateAsync(proposal, cancellationToken).ConfigureAwait(false);
        return activated;
    }

    public async Task<IntakeProposal?> RetryAsync(string id, CancellationToken cancellationToken)
    {
        var proposal = Get(id);
        if (proposal is null)
        {
            return null;
        }

        return await SubmitAsync(proposal.Kind, proposal.RawInput, cancellationToken)
            .ContinueWith(
                task =>
                {
                    Remove(id);
                    return task.Result;
                },
                cancellationToken,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default)
            .ConfigureAwait(false);
    }

    public async Task<IntakeProposal?> DisableAsync(string id, CancellationToken cancellationToken)
    {
        var proposal = Get(id);
        if (proposal is null)
        {
            return null;
        }

        var removed = proposal.Kind switch
        {
            IntakeKind.Source when proposal.ResolvedRecordId is not null =>
                await _knowledge.RemoveSourceAsync(proposal.ResolvedRecordId, cancellationToken)
                    .ConfigureAwait(false),
            IntakeKind.Skill when proposal.CanonicalName is not null =>
                await _knowledge.RemoveSkillAsync(proposal.CanonicalName, cancellationToken)
                    .ConfigureAwait(false),
            IntakeKind.Mcp when proposal.CanonicalName is not null =>
                await _knowledge.RemoveMcpServerAsync(proposal.CanonicalName, cancellationToken)
                    .ConfigureAwait(false),
            _ => false,
        };
        _ = removed;

        var updated = proposal with { Status = IntakeStatus.Disabled, UpdatedAt = DateTime.UtcNow };
        Save(updated);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<IntakeProposal?> RefreshAsync(string id, CancellationToken cancellationToken)
    {
        var proposal = Get(id);
        if (proposal is null)
        {
            return null;
        }

        return await ActivateAsync(proposal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var removed = Remove(id);
        if (removed)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<IntakeProposal> ActivateAsync(IntakeProposal proposal, CancellationToken cancellationToken)
    {
        IntakeProposal updated;
        try
        {
            updated = proposal.Kind switch
            {
                IntakeKind.Source => await ActivateSourceAsync(proposal, cancellationToken).ConfigureAwait(false),
                IntakeKind.Skill => await ActivateSkillAsync(proposal, cancellationToken).ConfigureAwait(false),
                IntakeKind.Mcp => await ActivateMcpAsync(proposal, cancellationToken).ConfigureAwait(false),
                _ => proposal,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            updated = proposal with
            {
                Status = IntakeStatus.Failed,
                Error = ex.Message,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        Save(updated);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<IntakeProposal> ActivateSourceAsync(IntakeProposal proposal, CancellationToken cancellationToken)
    {
        var pattern = proposal.ResolvedTarget ?? proposal.RawInput;
        var source = await _knowledge
            .AddSourceAsync(new CreateAuthoritativeSourceRequest(pattern, Enabled: true), cancellationToken)
            .ConfigureAwait(false);
        var status = source.Status switch
        {
            "ready" => IntakeStatus.Active,
            "pending" => IntakeStatus.Indexing,
            "failed" => IntakeStatus.Failed,
            _ => IntakeStatus.Indexing,
        };
        return proposal with
        {
            Status = status,
            ResolvedRecordId = source.Id,
            Error = source.Error,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private async Task<IntakeProposal> ActivateSkillAsync(IntakeProposal proposal, CancellationToken cancellationToken)
    {
        var name = proposal.CanonicalName ?? "skill-" + proposal.Id[..8];
        var location = proposal.ResolvedTarget ?? proposal.RawInput;
        var description = string.IsNullOrWhiteSpace(proposal.ResolvedDescription)
            ? "Imported through dashboard intake; description pending content review."
            : proposal.ResolvedDescription;
        var skill = await _knowledge
            .AddSkillAsync(new CreateDashboardSkillRequest(name, location, description, Enabled: true), cancellationToken)
            .ConfigureAwait(false);
        var status = skill.Status switch
        {
            "ready" => proposal.RequiresApproval ? IntakeStatus.Review : IntakeStatus.Active,
            "pending" => IntakeStatus.Fetching,
            _ => IntakeStatus.Failed,
        };
        return proposal with
        {
            Status = status,
            CanonicalName = skill.Name,
            ResolvedRecordId = skill.Name,
            Error = status == IntakeStatus.Failed ? $"Skill status: {skill.Status}." : null,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private async Task<IntakeProposal> ActivateMcpAsync(IntakeProposal proposal, CancellationToken cancellationToken)
    {
        var name = proposal.CanonicalName ?? "mcp-" + proposal.Id[..8];
        var endpoint = proposal.ResolvedTarget ?? proposal.RawInput;
        var server = await _knowledge
            .AddMcpServerAsync(new CreateDashboardMcpServerRequest(name, endpoint), cancellationToken)
            .ConfigureAwait(false);
        return proposal with
        {
            Status = server.Connected ? IntakeStatus.Active : IntakeStatus.Failed,
            CanonicalName = server.Name,
            ResolvedRecordId = server.Name,
            Error = server.Error,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private IntakeProposal? Get(string id)
    {
        lock (_sync)
        {
            return _proposals.GetValueOrDefault(id);
        }
    }

    private void Save(IntakeProposal proposal)
    {
        lock (_sync)
        {
            _proposals[proposal.Id] = proposal;
        }
    }

    private bool Remove(string id)
    {
        lock (_sync)
        {
            return _proposals.Remove(id);
        }
    }

    private static string GetDefaultStatePath()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("KARE_STATE_DIRECTORY");
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kare")
            : configuredDirectory;
        return Path.Combine(directory, "dashboard-intake.json");
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(_statePath);
            var state = JsonSerializer.Deserialize(bytes, DashboardJsonContext.Default.IntakeState);
            if (state?.Proposals is null)
            {
                return;
            }

            lock (_sync)
            {
                foreach (var proposal in state.Proposals)
                {
                    _proposals[proposal.Id] = proposal;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load persisted intake state from {Path}.", _statePath);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<IntakeProposal> proposals;
            lock (_sync)
            {
                proposals = [.. _proposals.Values];
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new IntakeState(proposals),
                DashboardJsonContext.Default.IntakeState);
            if (bytes.Length > MaxStateBytes)
            {
                _logger.LogWarning("Intake state exceeds the {Limit}-byte persistence limit; skipping write.", MaxStateBytes);
                return;
            }

            ProtectedStateFile.WriteAtomic(_statePath, bytes, StateBackupCount);
        }
        finally
        {
            _persistGate.Release();
        }
    }
}
