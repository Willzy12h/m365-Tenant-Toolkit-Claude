using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

public class AssessmentTests
{
    private static readonly FixedClock Clock = new();
    private static readonly AssessmentEngine Engine = new(Clock, "test");

    private static ControlFinding Find(AssessmentResult r, string id) => r.Findings.First(f => f.ControlId == id);

    [Fact]
    public void Incomplete_collection_is_unable_to_assess_not_missing()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Status = CaptureStatus.Error;
        snapshot.Collections["conditionalAccess"].Error = "403";
        snapshot.Complete = false;
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        Assert.Equal(FindingStatus.UnableToAssess, Find(result, "CA-001").Status);
        Assert.Equal(FindingStatus.UnableToAssess, Find(result, "CA-003").Status);
        Assert.Equal(FindingStatus.Missing, Find(result, "CMP-WIN-001").Status);
        Assert.Contains(result.Limitations, l => l.Contains("Conditional Access", StringComparison.Ordinal));
    }

    [Fact]
    public void Partially_collected_details_block_assessment()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["compliance"].DetailIncomplete = true;
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        Assert.Equal(FindingStatus.UnableToAssess, Find(result, "CMP-WIN-001").Status);
    }

    [Fact]
    public void Matching_enabled_policy_with_different_name_is_compliant()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Items.Add(TestData.ConditionalAccessPolicy("p1", "Previous MSP MFA", "enabled", new[] { TestData.Emergency }));
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        var finding = Find(result, "CA-001");
        Assert.Equal(FindingStatus.Compliant, finding.Status);
        Assert.Equal("Previous MSP MFA", finding.BestCandidate!.Name);
        Assert.True(finding.BestCandidate.SettingsMatch);
        Assert.Equal(FindingStatus.Missing, Find(result, "CA-003").Status);
    }

    /// <summary>
    /// A safe candidate is always created with the deploying operator excluded, so a toolkit-created policy never
    /// matches the recipe exactly until that exclusion is removed. That must read as a known, explained partial match
    /// against the toolkit's own object, not as an unrelated policy that happens to overlap.
    /// </summary>
    [Fact]
    public void Enabled_toolkit_policy_still_carrying_the_operator_exclusion_is_an_explained_partial_match()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Items.Add(
            TestData.ConditionalAccessPolicy("owned-1", "BDIT - CA-001 - Require MFA", "enabled", new[] { TestData.Emergency, TestData.Operator }));
        var mappings = TestData.Mappings();
        mappings.ByControl["CA-001"] = new ManagedObjectMapping { ControlId = "CA-001", ObjectId = "owned-1", Collection = "conditionalAccess" };

        var finding = Find(Engine.Assess(snapshot, standard, TestData.Profile(), mappings, Array.Empty<Deviation>(), "t"), "CA-001");

        Assert.Equal(FindingStatus.PartialMatch, finding.Status);
        Assert.True(finding.BestCandidate!.ToolkitManaged);
        Assert.False(finding.BestCandidate.SettingsMatch);
        Assert.Contains("operator excluded", finding.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not adopted automatically", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_disabled_policy_is_reported_as_not_enforced()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Items.Add(TestData.ConditionalAccessPolicy("p1", "BDIT - CA-001 - Require MFA", "disabled", new[] { TestData.Emergency }));
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        var finding = Find(result, "CA-001");
        Assert.Equal(FindingStatus.SettingsMatchNotEnforced, finding.Status);
        Assert.Equal(EnforcementState.Disabled, finding.BestCandidate!.Enforcement);
        Assert.True(finding.IsActionable);
    }

    [Fact]
    public void Same_name_with_different_settings_is_partial_match_with_property_differences()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Items.Add(TestData.ConditionalAccessPolicy("p1", "BDIT - CA-001 - Require MFA", "enabled", Array.Empty<string>(), withLocation: false));
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        var finding = Find(result, "CA-001");
        Assert.Equal(FindingStatus.PartialMatch, finding.Status);
        var candidate = finding.Candidates.Single();
        Assert.True(candidate.NameMatch);
        Assert.False(candidate.SettingsMatch);
        Assert.Contains(candidate.Differences, d => d.Setting == "conditions.users.excludeUsers" && !d.Match);
        Assert.Contains(candidate.Differences, d => d.Setting.StartsWith("conditions.locations", StringComparison.Ordinal) && d.Current.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Ids_are_resolved_to_names_in_differences()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["users"].Items.Add(new JsonObject { ["id"] = TestData.Emergency, ["displayName"] = "Break Glass 1", ["userPrincipalName"] = "bg1@test.example" });
        snapshot.Collections["namedLocations"].Items.Add(new JsonObject { ["id"] = TestData.Office, ["displayName"] = "LOC - BDIT Office" });
        snapshot.Collections["conditionalAccess"].Items.Add(TestData.ConditionalAccessPolicy("p1", "Other", "enabled", Array.Empty<string>()));
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        var candidate = Find(result, "CA-001").Candidates.Single();
        Assert.Contains(candidate.Differences, d => d.Setting == "conditions.users.excludeUsers" && d.Standard.Contains("Break Glass 1", StringComparison.Ordinal));
        Assert.Contains(candidate.Differences, d => d.Setting == "conditions.locations.excludeLocations" && d.Standard.Contains("LOC - BDIT Office", StringComparison.Ordinal));
    }

    [Fact]
    public void Approved_deviation_changes_status_but_never_hides_unknown_data()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        var deviation = new Deviation { Id = "d1", TenantId = TestData.TenantA, ControlId = "CA-003", Kind = DeviationKind.ApprovedDeviation, Reason = "Client uses third-party IdP", ApprovedBy = "Owner" };
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), new[] { deviation }, "t");
        Assert.Equal(FindingStatus.CompliantWithDeviation, Find(result, "CA-003").Status);
        Assert.Contains(Find(result, "CA-003").Notes, n => n.Contains("Missing", StringComparison.Ordinal));

        snapshot.Collections["conditionalAccess"].Status = CaptureStatus.Error;
        var unknown = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), new[] { deviation }, "t");
        Assert.Equal(FindingStatus.UnableToAssess, Find(unknown, "CA-003").Status);
    }

    [Fact]
    public void Not_applicable_deviation_and_licence_gap_are_reported_distinctly()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["licences"].Items.Clear();
        var na = new Deviation { Id = "d2", TenantId = TestData.TenantA, ControlId = "CMP-WIN-001", Kind = DeviationKind.NotApplicable, Reason = "No Windows devices", ApprovedBy = "Owner" };
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), new[] { na }, "t");
        Assert.Equal(FindingStatus.NotApplicable, Find(result, "CMP-WIN-001").Status);
        Assert.Equal(FindingStatus.LicenceUnavailable, Find(result, "CA-001").Status);
    }

    [Fact]
    public void Manual_controls_require_review_and_missing_parameters_do_not_become_missing()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        var result = Engine.Assess(snapshot, standard, TestData.Profile(office: ""), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        Assert.Equal(FindingStatus.RequiresManualReview, Find(result, "ID-001").Status);
        Assert.Equal(FindingStatus.RequiresManualReview, Find(result, "CA-001").Status);
        Assert.Contains("officeLocationId", Find(result, "CA-001").Reason, StringComparison.Ordinal);
        Assert.Equal(FindingStatus.Missing, Find(result, "CA-003").Status);
    }

    [Fact]
    public void Compliance_policy_with_assignment_is_compliant_and_without_is_not_enforced()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        var policy = ToolkitJson.ParseObject("""{"id":"c1","@odata.type":"#microsoft.graph.windows10CompliancePolicy","displayName":"Old","bitLockerEnabled":true,"secureBootEnabled":true,"osMinimumVersion":"10.0.26200.0","scheduledActionsForRule":[{"ruleName":"PasswordRequired","scheduledActionConfigurations":[{"actionType":"block","gracePeriodHours":0}]}],"_assignments":[]}""");
        snapshot.Collections["compliance"].Items.Add(policy);
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        Assert.Equal(FindingStatus.SettingsMatchNotEnforced, Find(result, "CMP-WIN-001").Status);

        policy["_assignments"] = new JsonArray(new JsonObject { ["id"] = "a1", ["target"] = new JsonObject { ["@odata.type"] = "#microsoft.graph.allDevicesAssignmentTarget" } });
        var assigned = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        Assert.Equal(FindingStatus.Compliant, Find(assigned, "CMP-WIN-001").Status);
    }

    [Fact]
    public void Summary_counts_actionable_by_severity()
    {
        var standard = TestData.Standard();
        var result = Engine.Assess(TestData.Snapshot(standard), standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        Assert.Equal(2, result.Summary.CriticalActionable);
        Assert.Equal(1, result.Summary.HighActionable);
        Assert.Equal(3, result.Summary.Missing);
        Assert.Equal(1, result.Summary.RequiresManualReview);
    }
}
