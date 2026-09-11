using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Collection;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class CollectorAndProfileTests
{
    private sealed class FailingAssignmentsClient : IGraphClient
    {
        private readonly FakeGraphClient _inner;
        public FailingAssignmentsClient(FakeGraphClient inner) => _inner = inner;
        public string TenantId => _inner.TenantId;
        public SessionMode Mode => _inner.Mode;
        public Task<JsonObject> GetAsync(GraphApi api, string path, CancellationToken ct) => _inner.GetAsync(api, path, ct);
        public Task<IReadOnlyList<JsonObject>> GetAllAsync(GraphApi api, string path, CancellationToken ct) =>
            path.EndsWith("/assignments", StringComparison.Ordinal) ? throw new PermissionException("GET", path, "Forbidden", "denied", "x") : _inner.GetAllAsync(api, path, ct);
        public Task<JsonObject> WriteAsync(GraphApi api, GraphWriteMethod method, string path, JsonObject payload, CancellationToken ct) => _inner.WriteAsync(api, method, path, payload, ct);
    }

    [Fact]
    public async Task Missing_assignment_details_make_the_snapshot_incomplete_not_absent()
    {
        var standard = TestData.Standard();
        var fake = new FakeGraphClient(standard);
        fake.Add("/deviceManagement/deviceCompliancePolicies", new JsonObject { ["id"] = "c1", ["displayName"] = "Policy" });
        var collector = new TenantCollector(NullLog.Instance, new FixedClock(), "test");
        var snapshot = await collector.CollectAsync(new FailingAssignmentsClient(fake), TestData.Session(), TestData.Profile(), standard, null, CancellationToken.None);
        Assert.False(snapshot.Complete);
        var capture = snapshot.Collections["compliance"];
        Assert.Equal(CaptureStatus.Collected, capture.Status);
        Assert.True(capture.DetailIncomplete);
        Assert.NotNull(capture.Items[0][TenantCollector.AssignmentsUnknownKey]);
        Assert.True(snapshot.Collections["conditionalAccess"].Usable);
    }

    [Fact]
    public async Task Collection_failure_is_recorded_per_collection_and_beta_use_is_flagged()
    {
        var standard = TestData.Standard();
        var fake = new FakeGraphClient(standard) { TenantId = TestData.TenantA };
        var collector = new TenantCollector(NullLog.Instance, new FixedClock(), "test");
        var snapshot = await collector.CollectAsync(fake, TestData.Session(), TestData.Profile(), standard, null, CancellationToken.None);
        Assert.Contains("settingsCatalogue", snapshot.BetaCollections);
        Assert.All(snapshot.Collections.Values, c => Assert.Equal(CaptureStatus.Collected, c.Status));
        await Assert.ThrowsAsync<TenantMismatchException>(() => collector.CollectAsync(fake, TestData.Session(TestData.TenantB), TestData.Profile(TestData.TenantB), standard, null, CancellationToken.None));
    }

    [Fact]
    public void Profile_validation_normalises_and_rejects_bad_values()
    {
        var now = DateTimeOffset.UtcNow;
        var p = ProfileValidator.Validate(new TenantProfile { Company = " Client ", TenantId = TestData.TenantA.ToUpperInvariant(), Parameters = new TenantParameters { EmergencyAccountIds = new List<string> { TestData.Emergency, TestData.Emergency } } }, now);
        Assert.Equal("Client", p.Company);
        Assert.Equal(TestData.TenantA, p.TenantId);
        Assert.Single(p.Parameters.EmergencyAccountIds);
        Assert.Throws<ConfigurationException>(() => ProfileValidator.Validate(new TenantProfile { Company = "x", TenantId = "not-a-guid" }, now));
        Assert.Throws<ConfigurationException>(() => ProfileValidator.Validate(new TenantProfile { Company = "x", TenantId = TestData.TenantA, Parameters = new TenantParameters { EmergencyAccountIds = new List<string> { "bad" } } }, now));
        Assert.Throws<ConfigurationException>(() => ProfileValidator.Validate(new TenantProfile { Company = "x", TenantId = TestData.TenantA, AssessmentClientId = TestData.ClientId, DeploymentClientId = TestData.ClientId }, now));
    }

    [Fact]
    public void Settings_reject_shared_app_for_deployment_and_resolve_clients()
    {
        Assert.Throws<ConfigurationException>(() => ToolkitSettings.Validate(new ToolkitSettings { DeploymentClientId = ToolkitSettings.MicrosoftGraphPowerShellClientId }));
        var s = ToolkitSettings.Validate(new ToolkitSettings { AssessmentClientId = "", DeploymentClientId = "" });
        var assessment = s.ResolveClient(SessionMode.Assessment, null);
        Assert.NotNull(assessment);
        Assert.True(assessment!.Value.IsSharedFallback);
        Assert.Null(s.ResolveClient(SessionMode.Deployment, null));
        var withProfile = s.ResolveClient(SessionMode.Deployment, new TenantProfile { DeploymentClientId = TestData.ClientId });
        Assert.Equal(TestData.ClientId, withProfile!.Value.ClientId);
        var strict = ToolkitSettings.Validate(new ToolkitSettings { AllowMicrosoftGraphPowerShellFallback = false });
        Assert.Null(strict.ResolveClient(SessionMode.Assessment, null));
    }

    [Fact]
    public void Paths_resolve_from_an_explicit_root_and_reject_missing_standards()
    {
        using var root = new TempRoot();
        Assert.Equal(root.Root, root.Paths.Root);
        Assert.EndsWith("standards", root.Paths.StandardsDirectory, StringComparison.Ordinal);
        Assert.Throws<ConfigurationException>(() => ToolkitPaths.Resolve(Path.Combine(root.Root, "logs")));
        Assert.Throws<ConfigurationException>(() => root.Paths.TenantDirectory("not-a-guid"));
    }
}
