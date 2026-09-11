using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Evidence;
using BDIT.TenantToolkit.Engine.Planning;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class TenantBindingTests
{
    private static readonly FixedClock Clock = new();

    [Fact]
    public void Snapshot_for_another_tenant_cannot_be_loaded_under_this_tenant()
    {
        using var root = new TempRoot();
        var store = new EvidenceStore(root.Paths, NullLog.Instance);
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard, TestData.TenantB);
        store.SaveSnapshot(snapshot);
        Assert.Null(store.LoadSnapshot(TestData.TenantA, snapshot.Id));
        Assert.NotNull(store.LoadSnapshot(TestData.TenantB, snapshot.Id));
        Assert.Empty(store.ListSnapshots(TestData.TenantA));
    }

    [Fact]
    public void Planner_rejects_snapshot_from_another_tenant()
    {
        var standard = TestData.Standard();
        var planner = new DeploymentPlanner(Clock, "test");
        Assert.Throws<TenantMismatchException>(() => planner.Build(new PlanRequest
        {
            Profile = TestData.Profile(),
            Standard = standard,
            Snapshot = TestData.Snapshot(standard, TestData.TenantB),
            Mappings = TestData.Mappings(),
            Deviations = Array.Empty<Deviation>(),
            SelectedControlIds = new[] { "CA-001" },
            Session = TestData.Session()
        }));
    }

    [Fact]
    public void Plan_for_tenant_A_cannot_execute_against_tenant_B()
    {
        var standard = TestData.Standard();
        var profileA = TestData.Profile();
        var snapshotA = TestData.Snapshot(standard);
        var plan = new DeploymentPlanner(Clock, "test").Build(new PlanRequest
        {
            Profile = profileA, Standard = standard, Snapshot = snapshotA, Mappings = TestData.Mappings(),
            Deviations = Array.Empty<Deviation>(), SelectedControlIds = new[] { "CA-001" }, Session = TestData.Session()
        });
        Assert.Throws<TenantMismatchException>(() => DeploymentPlanner.Validate(plan, new PlanValidationContext
        {
            Profile = TestData.Profile(TestData.TenantB),
            Standard = standard,
            Snapshot = TestData.Snapshot(standard, TestData.TenantB),
            Mappings = TestData.Mappings(TestData.TenantB),
            Session = TestData.Session(TestData.TenantB),
            AcknowledgedSnapshotId = snapshotA.Id,
            Now = Clock.UtcNow
        }));
    }

    [Fact]
    public void Assessment_rejects_mismatched_snapshot_or_mappings()
    {
        var standard = TestData.Standard();
        var engine = new AssessmentEngine(Clock, "test");
        Assert.Throws<TenantMismatchException>(() => engine.Assess(TestData.Snapshot(standard, TestData.TenantB), standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t"));
        Assert.Throws<TenantMismatchException>(() => engine.Assess(TestData.Snapshot(standard), standard, TestData.Profile(), TestData.Mappings(TestData.TenantB), Array.Empty<Deviation>(), "t"));
    }

    [Fact]
    public void Deviations_from_another_tenant_are_rejected_by_the_store()
    {
        using var root = new TempRoot();
        var store = new EvidenceStore(root.Paths, NullLog.Instance);
        var foreign = new Deviation { Id = Guid.NewGuid().ToString(), TenantId = TestData.TenantB, ControlId = "CA-001", Reason = "x" };
        Assert.Throws<TenantMismatchException>(() => store.SaveDeviations(TestData.TenantA, new[] { foreign }));
    }
}
