using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Engine.Assessment;
using Xunit;

namespace BDIT.TenantToolkit.Tests;

/// <summary>
/// Equivalence answers "is this control already covered by something the client built themselves?". These tests pin the
/// boundary: it must recognise a differently named, differently shaped policy, and it must never turn that recognition
/// into a claim of compliance.
/// </summary>
public class EquivalenceTests
{
    private static readonly FixedClock Clock = new();
    private static readonly AssessmentEngine Engine = new(Clock, "test");

    private static ControlFinding Assess(JsonObject? policy)
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        if (policy is not null) snapshot.Collections["conditionalAccess"].Items.Add(policy);
        var result = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t");
        return result.Findings.First(f => f.ControlId == "CA-001");
    }

    /// <summary>A client's own MFA policy: right intent, unrecognisable name, different shape. This is the common case.</summary>
    private static JsonObject ClientMfaPolicy(string state = "enabled", string? grant = "mfa", bool allUsers = true, bool allApps = true, string[]? excludeUsers = null)
    {
        var policy = new JsonObject
        {
            ["id"] = "client-1",
            ["displayName"] = "MFA for staff (set up by previous IT)",
            ["state"] = state,
            ["conditions"] = new JsonObject
            {
                ["users"] = new JsonObject
                {
                    ["includeUsers"] = new JsonArray(allUsers ? "All" : "11111111-1111-4111-8111-111111111111"),
                    ["excludeUsers"] = new JsonArray((excludeUsers ?? Array.Empty<string>()).Select(u => (JsonNode?)u).ToArray())
                },
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray(allApps ? "All" : "Office365") }
            }
        };
        if (grant is not null) policy["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray(grant) };
        return policy;
    }

    [Fact]
    public void Differently_named_policy_meeting_every_condition_is_a_partial_match_not_missing()
    {
        var finding = Assess(ClientMfaPolicy());

        Assert.Equal(FindingStatus.PartialMatch, finding.Status);
        Assert.Contains("Equivalent configuration observed", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("MFA for staff (set up by previous IT)", finding.Reason, StringComparison.Ordinal);
        var observation = Assert.Single(finding.Equivalence, e => e.Covered);
        Assert.All(observation.Signals.Where(s => s.Required), s => Assert.True(s.Matched));
    }

    /// <summary>The central safety property: recognising equivalent configuration never asserts compliance.</summary>
    [Fact]
    public void Equivalence_never_reports_compliant()
    {
        var finding = Assess(ClientMfaPolicy());

        Assert.NotEqual(FindingStatus.Compliant, finding.Status);
        Assert.NotEqual(FindingStatus.CompliantWithDeviation, finding.Status);
        Assert.Contains("not counted as compliant", finding.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "mfa")]   // only some users
    [InlineData(true, false, "mfa")]   // only some applications
    [InlineData(true, true, "block")]  // does something else entirely
    [InlineData(true, true, null)]     // no grant controls at all
    public void A_policy_failing_any_required_condition_is_not_coverage(bool allUsers, bool allApps, string? grant)
    {
        var finding = Assess(ClientMfaPolicy(allUsers: allUsers, allApps: allApps, grant: grant));

        Assert.Equal(FindingStatus.Missing, finding.Status);
        Assert.DoesNotContain(finding.Equivalence, e => e.Covered);
    }

    /// <summary>A policy can satisfy every signal and still protect almost nobody; the exclusions must be visible.</summary>
    [Fact]
    public void Exclusions_are_reported_as_caveats_on_a_covering_policy()
    {
        var finding = Assess(ClientMfaPolicy(excludeUsers: new[] { "a1111111-1111-4111-8111-111111111111", "b1111111-1111-4111-8111-111111111111" }));

        var observation = Assert.Single(finding.Equivalence, e => e.Covered);
        Assert.Contains(observation.Caveats, c => c.Contains("Excludes named users", StringComparison.Ordinal));
        Assert.Contains(finding.Notes, n => n.Contains("Qualifies the match", StringComparison.Ordinal));
    }

    /// <summary>A matching policy that is switched off covers nothing, and the finding must say so.</summary>
    [Fact]
    public void A_disabled_equivalent_policy_is_flagged_as_not_protecting_anyone()
    {
        var finding = Assess(ClientMfaPolicy(state: "disabled"));

        Assert.Equal(FindingStatus.PartialMatch, finding.Status);
        Assert.Contains(finding.Notes, n => n.Contains("not protecting anyone", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_signal_records_the_observed_value_so_the_claim_can_be_checked()
    {
        var finding = Assess(ClientMfaPolicy());

        var observation = Assert.Single(finding.Equivalence, e => e.Covered);
        Assert.All(observation.Signals, s =>
        {
            Assert.NotEmpty(s.Label);
            Assert.NotEmpty(s.Expected);
            Assert.NotEmpty(s.Observed);
        });
    }

    [Fact]
    public void An_exact_recipe_match_keeps_its_stronger_result_rather_than_being_downgraded()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Items.Add(
            TestData.ConditionalAccessPolicy("p1", "Anything at all", "enabled", new[] { TestData.Emergency }));

        var finding = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t")
            .Findings.First(f => f.ControlId == "CA-001");

        Assert.Equal(FindingStatus.Compliant, finding.Status);
    }

    [Fact]
    public void Unreadable_collections_never_produce_equivalence_claims()
    {
        var standard = TestData.Standard();
        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Status = CaptureStatus.Error;
        snapshot.Collections["conditionalAccess"].Error = "403";

        var finding = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t")
            .Findings.First(f => f.ControlId == "CA-001");

        Assert.Equal(FindingStatus.UnableToAssess, finding.Status);
        Assert.Empty(finding.Equivalence);
    }

    [Theory]
    [InlineData(SignalOperator.Contains, "[\"All\"]", "\"All\"", true)]
    [InlineData(SignalOperator.Contains, "[\"all\"]", "\"All\"", true)]      // Graph is inconsistent about casing
    [InlineData(SignalOperator.Contains, "[\"None\"]", "\"All\"", false)]
    [InlineData(SignalOperator.ContainsAll, "[\"a\",\"b\"]", "[\"a\",\"b\"]", true)]
    [InlineData(SignalOperator.ContainsAll, "[\"a\"]", "[\"a\",\"b\"]", false)]
    [InlineData(SignalOperator.AtMost, "5", "10", true)]
    [InlineData(SignalOperator.AtLeast, "5", "10", false)]
    public void Operators_behave_as_declared(SignalOperator op, string observedJson, string valueJson, bool expected)
    {
        var signal = new EquivalenceSignal { Key = "k", Label = "l", Path = "p", Operator = op, Value = ToolkitJson.ParseNode(valueJson) };

        Assert.Equal(expected, EquivalenceEvaluator.Matches(signal, ToolkitJson.ParseNode(observedJson)));
    }

    /// <summary>Alternative routes to the same outcome (MFA control or authentication strength) are ORed within a group.</summary>
    [Fact]
    public void Grouped_signals_are_alternatives_so_either_route_counts_as_coverage()
    {
        var policy = ClientMfaPolicy(grant: null);
        policy["grantControls"] = new JsonObject { ["operator"] = "OR", ["authenticationStrength"] = new JsonObject { ["id"] = "00000000-0000-0000-0000-000000000004" } };

        var standard = TestData.Standard();
        var control = standard.Controls.First(c => c.Id == "CA-001");
        control.Equivalence!.Signals.Add(new EquivalenceSignal
        {
            Key = "mfaStrength",
            Label = "Requires an authentication strength",
            Path = "grantControls.authenticationStrength",
            Operator = SignalOperator.Present,
            Group = "mfa"
        });
        foreach (var s in control.Equivalence.Signals.Where(s => s.Key == "mfa")) s.Group = "mfa";

        var snapshot = TestData.Snapshot(standard);
        snapshot.Collections["conditionalAccess"].Items.Add(policy);
        var finding = Engine.Assess(snapshot, standard, TestData.Profile(), TestData.Mappings(), Array.Empty<Deviation>(), "t")
            .Findings.First(f => f.ControlId == "CA-001");

        Assert.Equal(FindingStatus.PartialMatch, finding.Status);
        Assert.Contains(finding.Equivalence, e => e.Covered);
    }

    [Fact]
    public void A_signal_against_a_missing_property_never_matches()
    {
        var signal = new EquivalenceSignal { Key = "k", Label = "l", Path = "p", Operator = SignalOperator.Contains, Value = JsonValue.Create("All") };

        Assert.False(EquivalenceEvaluator.Matches(signal, null));
    }
}
