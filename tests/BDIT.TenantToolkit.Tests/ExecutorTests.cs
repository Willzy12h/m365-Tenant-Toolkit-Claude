using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Core.Safety;
using BDIT.TenantToolkit.Engine.Collection;
using BDIT.TenantToolkit.Engine.Evidence;
using BDIT.TenantToolkit.Engine.Execution;
using BDIT.TenantToolkit.Engine.Planning;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class ExecutorTests
{
    private sealed class Harness : IDisposable
    {
        public TempRoot Root { get; } = new();
        public FixedClock Clock { get; } = new();
        public StandardCatalogue Standard { get; } = TestData.Standard();
        public EvidenceStore Evidence { get; }
        public FakeGraphClient Graph { get; }
        public TenantCollector Collector { get; }
        public DeploymentExecutor Executor { get; }
        public TenantProfile Profile { get; } = TestData.Profile();
        public TenantSession Session { get; } = TestData.Session();

        public Harness()
        {
            Evidence = new EvidenceStore(Root.Paths, NullLog.Instance);
            Graph = new FakeGraphClient(Standard);
            Collector = new TenantCollector(NullLog.Instance, Clock, "test");
            Executor = new DeploymentExecutor(Evidence, Collector, NullLog.Instance, Clock, "test");
        }

        public async Task<(TenantSnapshot Snapshot, DeploymentPlan Plan)> CaptureAndPlanAsync(params string[] ids)
        {
            var snapshot = await Collector.CollectAsync(Graph, Session, Profile, Standard, null, CancellationToken.None);
            Evidence.SaveSnapshot(snapshot);
            var plan = new DeploymentPlanner(Clock, "test").Build(new PlanRequest
            {
                Profile = Profile, Standard = Standard, Snapshot = snapshot, Mappings = Evidence.LoadMappings(Profile.TenantId),
                Deviations = Array.Empty<Deviation>(), SelectedControlIds = ids, Session = Session
            });
            return (snapshot, plan);
        }

        public Task<DeploymentRun> RunAsync(DeploymentPlan plan, TenantSnapshot snapshot, DeploymentControl? control = null) =>
            Executor.StartAsync(new ExecutionRequest
            {
                Plan = plan, Profile = Profile, Standard = Standard, Snapshot = snapshot, Mappings = Evidence.LoadMappings(Profile.TenantId), Session = Session, Graph = Graph
            }, control ?? new DeploymentControl(), null);

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public async Task Successful_run_writes_reads_back_maps_and_captures_after_evidence()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001", "CMP-WIN-001");
        var run = await h.RunAsync(plan, snapshot);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, h.Graph.Writes.Count);
        Assert.All(run.Results, r => Assert.Equal(ResultStatus.Completed, r.Status));
        Assert.All(run.Results, r => Assert.Equal(ConfigurationVerification.Pass, r.Configuration));
        Assert.Contains(run.Results, r => r.Verification.Contains("Functional", StringComparison.Ordinal));
        Assert.NotNull(run.AfterSnapshotId);
        Assert.NotNull(h.Evidence.LoadSnapshot(TestData.TenantA, run.AfterSnapshotId!));
        Assert.NotNull(h.Evidence.LoadRun(TestData.TenantA, run.Id));

        var mappings = h.Evidence.LoadMappings(TestData.TenantA);
        Assert.Equal(run.Results[0].ObjectId, mappings.ByControl["CA-001"].ObjectId);
        Assert.Equal(TestData.Operator, mappings.ByControl["CA-001"].OperatorExclusion!.ObjectId);

        var written = h.Graph.Writes[0].Payload;
        Assert.Equal("disabled", ConditionalAccessSafety.State(written));
        Assert.Contains(TestData.Operator, ConditionalAccessSafety.ExcludedUsers(written));
        Assert.Contains(h.Evidence.ReadJournal(TestData.TenantA, run.Id), j => j.Message.Contains("WRITE INTENT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_deployment_creates_no_duplicates()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001");
        await h.RunAsync(plan, snapshot);
        Assert.Single(h.Graph.Writes);

        var (snapshot2, plan2) = await h.CaptureAndPlanAsync("CA-001");
        Assert.Equal(PlanAction.NoChange, plan2.Rows[0].Action);
        Assert.Throws<PlanValidationException>(() => DeploymentPlanner.Validate(plan2, new PlanValidationContext
        {
            Profile = h.Profile, Standard = h.Standard, Snapshot = snapshot2, Mappings = h.Evidence.LoadMappings(TestData.TenantA), Session = h.Session, AcknowledgedSnapshotId = snapshot2.Id, Now = h.Clock.UtcNow
        }));
        Assert.Single(h.Graph.Collection("/identity/conditionalAccess/policies"));
    }

    [Fact]
    public async Task Ambiguous_write_failure_stops_the_run_without_mapping()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001", "CA-003");
        h.Graph.ThrowOnWrite = new AmbiguousWriteException("Simulated timeout", null);
        var run = await h.RunAsync(plan, snapshot);

        Assert.Equal(RunStatus.ReviewRequired, run.Status);
        Assert.Single(h.Graph.Writes);
        Assert.Equal(ResultStatus.Error, run.Results[0].Status);
        Assert.Equal(ConfigurationVerification.Unknown, run.Results[0].Configuration);
        Assert.Equal(ResultStatus.NotRun, run.Results[1].Status);
        Assert.Empty(h.Evidence.LoadMappings(TestData.TenantA).ByControl);
        Assert.NotNull(run.AfterSnapshotId);
    }

    [Fact]
    public async Task Name_collision_after_planning_blocks_the_write()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001");
        h.Graph.Add("/identity/conditionalAccess/policies", TestData.ConditionalAccessPolicy("x1", "BDIT - CA-001 - Require MFA", "enabled", Array.Empty<string>(), withLocation: false));
        var run = await h.RunAsync(plan, snapshot);
        Assert.Equal(RunStatus.ReviewRequired, run.Status);
        Assert.Empty(h.Graph.Writes);
        Assert.Contains("appeared", run.Results[0].Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_graph_session_cannot_execute()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001");
        h.Graph.Mode = SessionMode.Assessment;
        await Assert.ThrowsAsync<WriteDeniedException>(() => h.RunAsync(plan, snapshot));
        Assert.Empty(h.Graph.Writes);
    }

    [Fact]
    public async Task Readback_mismatch_marks_run_for_review_but_keeps_the_object_mapped()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001");
        h.Graph.MutateReadback = o => { o["grantControls"]!["builtInControls"] = new JsonArray("block"); return o; };
        var run = await h.RunAsync(plan, snapshot);
        Assert.Equal(RunStatus.ReviewRequired, run.Status);
        Assert.Equal(ConfigurationVerification.Unknown, run.Results[0].Configuration);
        Assert.Equal(ResultStatus.Completed, run.Results[0].Status);
        Assert.Single(h.Evidence.LoadMappings(TestData.TenantA).ByControl);
    }

    [Fact]
    public async Task Stop_request_finishes_the_current_write_and_skips_the_rest()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001", "CA-003");
        var control = new DeploymentControl();
        h.Graph.BeforeWrite = (_, _) => { control.Stop(); return Task.CompletedTask; };
        var run = await h.RunAsync(plan, snapshot, control);
        Assert.Equal(RunStatus.Stopped, run.Status);
        Assert.Single(h.Graph.Writes);
        Assert.Equal(ResultStatus.Completed, run.Results[0].Status);
        Assert.Equal(ResultStatus.NotRun, run.Results[1].Status);
        Assert.NotNull(run.AfterSnapshotId);
    }

    [Fact]
    public async Task Shutdown_waits_for_the_in_flight_write()
    {
        using var h = new Harness();
        var (snapshot, plan) = await h.CaptureAndPlanAsync("CA-001", "CA-003");
        var gate = new TaskCompletionSource();
        h.Graph.BeforeWrite = async (_, _) => await gate.Task;
        var control = new DeploymentControl();
        var runTask = h.RunAsync(plan, snapshot, control);
        await Task.Delay(50);
        Assert.True(h.Executor.IsRunning);

        var waiter = h.Executor.WaitForCompletionAsync(control);
        Assert.False(waiter.IsCompleted);
        gate.SetResult();
        await waiter;
        var run = await runTask;
        Assert.False(h.Executor.IsRunning);
        Assert.Single(h.Graph.Writes);
        Assert.Equal(RunStatus.Stopped, run.Status);
        Assert.Equal(ResultStatus.Completed, run.Results[0].Status);
    }

    [Fact]
    public void Interrupted_runs_are_marked_honestly_at_start_up()
    {
        using var h = new Harness();
        var run = new DeploymentRun { Id = Guid.NewGuid().ToString(), TenantId = TestData.TenantA, StartedAt = "2026-09-11T09:00:00.000Z", Status = RunStatus.Running,
            Results = new List<RunResult> { new() { ControlId = "CA-001", Status = ResultStatus.InProgress }, new() { ControlId = "CA-003", Status = ResultStatus.Pending } } };
        h.Evidence.SaveRun(run);
        Assert.Equal(1, h.Evidence.MarkInterruptedRuns(TestData.TenantA));
        var loaded = h.Evidence.LoadRun(TestData.TenantA, run.Id)!;
        Assert.Equal(RunStatus.Interrupted, loaded.Status);
        Assert.Equal(ResultStatus.Error, loaded.Results[0].Status);
        Assert.Equal(ResultStatus.NotRun, loaded.Results[1].Status);
        Assert.Equal(0, h.Evidence.MarkInterruptedRuns(TestData.TenantA));
    }
}
