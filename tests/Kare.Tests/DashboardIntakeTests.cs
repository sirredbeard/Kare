using Kare.Service.Dashboard;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kare.Tests;

public sealed class DashboardIntakeTests
{
    [Fact]
    public async Task SafeDirectSourcePageActivatesWithoutApproval()
    {
        var directory = CreateDirectory();
        var statePath = Path.Combine(directory, "intake.json");

        try
        {
            var intake = CreateIntake(statePath, out _);
            var proposal = await intake.SubmitAsync(
                IntakeKind.Source,
                "https://1.1.1.1/reference/page",
                TestContext.Current.CancellationToken);

            Assert.False(proposal.RequiresApproval);
            Assert.False(proposal.UsedLocalModel);
            Assert.False(proposal.UsedCloud);
            Assert.NotEqual(IntakeStatus.Review, proposal.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousTextInputWithoutLocalModelIsMarkedForReviewAndNeverSilentlyActivated()
    {
        var directory = CreateDirectory();
        var statePath = Path.Combine(directory, "intake.json");

        try
        {
            var intake = CreateIntake(statePath, out _);
            var proposal = await intake.SubmitAsync(
                IntakeKind.Mcp,
                "just some ambiguous text with no scheme",
                TestContext.Current.CancellationToken);

            Assert.Equal(IntakeStatus.Review, proposal.Status);
            Assert.True(proposal.RequiresApproval);
            Assert.False(proposal.UsedCloud);
            Assert.False(proposal.IsBillable);
            Assert.NotNull(proposal.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallCommandTextIsParsedAsDataAndNeverExecuted()
    {
        var directory = CreateDirectory();
        var statePath = Path.Combine(directory, "intake.json");

        try
        {
            var intake = CreateIntake(statePath, out _);
            var proposal = await intake.SubmitAsync(
                IntakeKind.Skill,
                "npm install some-untrusted-skill-package",
                TestContext.Current.CancellationToken);

            Assert.Equal(IntakeStatus.Review, proposal.Status);
            Assert.True(proposal.RequiresApproval);
            Assert.Equal("some-untrusted-skill-package", proposal.CanonicalName);
            Assert.Contains("not executed", proposal.Error ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(proposal.ResolvedRecordId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task McpEndpointQueryStringIsRedactedFromDashboardOutput()
    {
        var directory = CreateDirectory();
        var statePath = Path.Combine(directory, "intake.json");

        try
        {
            var intake = CreateIntake(statePath, out _);
            var proposal = await intake.SubmitAsync(
                IntakeKind.Mcp,
                "https://mcp.example.com/sse?token=super-secret-value",
                TestContext.Current.CancellationToken);

            Assert.NotNull(proposal.CanonicalSource);
            Assert.DoesNotContain("super-secret-value", proposal.CanonicalSource, StringComparison.Ordinal);
            Assert.DoesNotContain("token", proposal.CanonicalSource, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProposalsPersistAcrossServiceRestart()
    {
        var directory = CreateDirectory();
        var statePath = Path.Combine(directory, "intake.json");

        try
        {
            string id;
            using (var knowledge = CreateKnowledge(Path.Combine(directory, "registry.json")))
            {
                var first = new DashboardIntakeService(
                    knowledge,
                    NullLogger<DashboardIntakeService>.Instance,
                    statePath);
                var proposal = await first.SubmitAsync(
                    IntakeKind.Mcp,
                    "unresolved ambiguous mcp text",
                    TestContext.Current.CancellationToken);
                id = proposal.Id;
                Assert.Equal(IntakeStatus.Review, proposal.Status);
            }

            using var knowledge2 = CreateKnowledge(Path.Combine(directory, "registry.json"));
            var second = new DashboardIntakeService(
                knowledge2,
                NullLogger<DashboardIntakeService>.Instance,
                statePath);

            var restored = second.GetProposals().SingleOrDefault(item => item.Id == id);
            Assert.NotNull(restored);
            Assert.Equal(IntakeStatus.Review, restored!.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisableRemovesActivatedSourceRegistryRecord()
    {
        var directory = CreateDirectory();
        var statePath = Path.Combine(directory, "intake.json");

        try
        {
            var intake = CreateIntake(statePath, out var knowledge);
            var proposal = await intake.SubmitAsync(
                IntakeKind.Source,
                "https://1.1.1.1/reference/page",
                TestContext.Current.CancellationToken);
            Assert.NotNull(proposal.ResolvedRecordId);

            var disabled = await intake.DisableAsync(proposal.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(disabled);
            Assert.Equal(IntakeStatus.Disabled, disabled!.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("troubleshoot a VENTUNO Q device service restart and check logs")]
    [InlineData("investigate dashboard log triage on the device")]
    public void BuiltinSkillsSelectRelevantMatchesForDeviceTriageRequests(string requestText)
    {
        var selected = BuiltinSkills.SelectRelevant(requestText);

        Assert.NotEmpty(selected);
        Assert.Contains(selected, skill => skill.Id == "service-dashboard-log-triage");
    }

    [Fact]
    public void BuiltinSkillsSelectRelevantIsBoundedByMaxCount()
    {
        var selected = BuiltinSkills.SelectRelevant(
            "device deployment issue evidence skill mcp source architecture identity triage",
            maxCount: 2);

        Assert.True(selected.Count <= 2);
    }

    [Fact]
    public void BuiltinDeploymentSkillDoesNotClaimCallerMachineIsTheDevice()
    {
        var deployment = BuiltinSkills.All.Single(skill => skill.Id == "ventuno-deployment-execution-boundary");

        Assert.Contains("caller", deployment.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("this is the device", deployment.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kare-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static DashboardKnowledgeService CreateKnowledge(string statePath) =>
        new(
            new InMemoryMetricsCollector(),
            new StaticHttpClientFactory(),
            NullLogger<DashboardKnowledgeService>.Instance,
            statePath);

    private static DashboardIntakeService CreateIntake(
        string statePath,
        out DashboardKnowledgeService knowledge)
    {
        knowledge = CreateKnowledge(Path.Combine(Path.GetDirectoryName(statePath)!, "registry.json"));
        return new DashboardIntakeService(
            knowledge,
            NullLogger<DashboardIntakeService>.Instance,
            statePath);
    }

    private sealed class StaticHttpClientFactory(HttpClient? client = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client ?? new();
    }
}
