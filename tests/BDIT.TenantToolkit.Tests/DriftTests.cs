using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Drift;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class DriftTests
{
    private static readonly FixedClock Clock = new();
    private static readonly DriftAnalyser Analyser = new(Clock, new AssessmentEngine(Clock, "test"));

    [Fact]
    public void Different_tenants_are_rejected()
    {
        var standard = TestData.Standard();
        Assert.Throws<TenantMismatchException>(() => Analyser.Compare(TestData.Snapshot(standard), TestData.Snapshot(standard, TestData.TenantB), standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>()));
    }

    [Fact]
    public void Managed_object_removal_and_external_modification_are_classified()
    {
        var standard = TestData.Standard();
        var policy = TestData.ConditionalAccessPolicy("owned-1", "BDIT - CA-001 - Require MFA", "disabled", new[] { TestData.Emergency });
        var mappings = TestData.Mappings();
        var applied = (JsonObject)policy.DeepClone();
        applied.Remove("id");
        applied.Remove("createdDateTime");
        mappings.ByControl["CA-001"] = new ManagedObjectMapping { ControlId = "CA-001", ObjectId = "owned-1", Collection = "conditionalAccess", LastApplied = applied };

        var before = TestData.Snapshot(standard);
        before.Collections["conditionalAccess"].Items.Add(policy);
        var removed = TestData.Snapshot(standard, capturedAt: Clock.UtcNow);
        var report = Analyser.Compare(before, removed, standard, TestData.Profile(), mappings, Array.Empty<Deviation>());
        var item = Assert.Single(report.Items, i => i.ObjectId == "owned-1");
        Assert.Equal(DriftChange.Removed, item.Change);
        Assert.Equal(DriftClassification.ManagedObjectRemoved, item.Classification);
        Assert.Equal("CA-001", item.ControlId);
        Assert.Contains(report.ControlChanges, c => c.ControlId == "CA-001" && c.Regression);

        var modified = TestData.Snapshot(standard, capturedAt: Clock.UtcNow);
        var changed = (JsonObject)policy.DeepClone();
        changed["state"] = "enabled";
        modified.Collections["conditionalAccess"].Items.Add(changed);
        var report2 = Analyser.Compare(before, modified, standard, TestData.Profile(), mappings, Array.Empty<Deviation>());
        var item2 = Assert.Single(report2.Items, i => i.ObjectId == "owned-1");
        Assert.Equal(DriftChange.Changed, item2.Change);
        Assert.Equal(DriftClassification.ManagedObjectModifiedExternally, item2.Classification);
        Assert.Contains(item2.Differences, d => d.Setting == "state" && d.Standard == "disabled" && d.Current == "enabled");
        Assert.Contains(report2.ControlChanges, c => c.ControlId == "CA-001" && !c.Regression && c.After == FindingStatus.Compliant);
    }

    [Fact]
    public void Metadata_only_changes_on_managed_objects_are_not_external_modifications()
    {
        var standard = TestData.Standard();
        var policy = TestData.ConditionalAccessPolicy("owned-1", "BDIT - CA-001 - Require MFA", "disabled", new[] { TestData.Emergency });
        var mappings = TestData.Mappings();
        var applied = (JsonObject)policy.DeepClone();
        applied.Remove("id");
        applied.Remove("createdDateTime");
        mappings.ByControl["CA-001"] = new ManagedObjectMapping { ControlId = "CA-001", ObjectId = "owned-1", Collection = "conditionalAccess", LastApplied = applied };
        var before = TestData.Snapshot(standard);
        before.Collections["conditionalAccess"].Items.Add(policy);
        var after = TestData.Snapshot(standard, capturedAt: Clock.UtcNow);
        var withExtra = (JsonObject)policy.DeepClone();
        withExtra["templateId"] = "server-added";
        after.Collections["conditionalAccess"].Items.Add(withExtra);
        var report = Analyser.Compare(before, after, standard, TestData.Profile(), mappings, Array.Empty<Deviation>());
        Assert.Equal(DriftClassification.ManagedObjectMetadataOnly, Assert.Single(report.Items, i => i.ObjectId == "owned-1").Classification);
    }

    [Fact]
    public void Unmanaged_additions_and_incomplete_collections_are_reported()
    {
        var standard = TestData.Standard();
        var before = TestData.Snapshot(standard);
        var after = TestData.Snapshot(standard, capturedAt: Clock.UtcNow);
        after.Collections["conditionalAccess"].Items.Add(TestData.ConditionalAccessPolicy("new", "Someone else", "enabled", Array.Empty<string>()));
        after.Collections["compliance"].Status = CaptureStatus.Error;
        var report = Analyser.Compare(before, after, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>());
        Assert.Contains(report.Items, i => i.ObjectId == "new" && i.Change == DriftChange.Added && i.Classification == DriftClassification.ExternalChange);
        Assert.Contains(report.Items, i => i.Collection == "compliance" && i.Change == DriftChange.UnableToAssess);
        Assert.Contains(report.ControlChanges, c => c.ControlId == "CMP-WIN-001" && c.After == FindingStatus.UnableToAssess && !c.Regression);
    }

    [Fact]
    public void Standard_release_change_is_noted()
    {
        var standard = TestData.Standard();
        var before = TestData.Snapshot(standard);
        before.StandardRelease = "old.1";
        var report = Analyser.Compare(before, TestData.Snapshot(standard, capturedAt: Clock.UtcNow), standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>());
        Assert.True(report.StandardReleaseChanged);
        Assert.Contains(report.Notes, n => n.Contains("different standard releases", StringComparison.Ordinal));
    }
}
