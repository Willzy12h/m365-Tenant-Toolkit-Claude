using System.Windows;
using System.Windows.Controls;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.App.Views;

/// <summary>Final gate before a write: the engineer must type the connected tenant ID in full and sees every planned write.</summary>
public partial class ConfirmTenantDialog : Window
{
    private readonly string _expectedTenantId;

    public string TypedTenantId => TypedTenantIdBox.Text.Trim();

    public ConfirmTenantDialog(TenantProfile profile, DeploymentPlan plan)
    {
        InitializeComponent();
        _expectedTenantId = profile.TenantId;
        var writes = plan.WriteRows.ToList();
        SummaryText.Text = $"{profile.Company} · {plan.TenantName} · {writes.Count(r => r.Action == PlanAction.Create)} object(s) to create, {writes.Count(r => r.Action == PlanAction.Update)} to update · plan {plan.Id} (digest {plan.PlanDigest[..12]}…)";
        TenantIdText.Text = profile.TenantId;
        foreach (var row in writes)
            ChangeList.Items.Add($"{row.Action,-7} {row.ControlId,-12} {row.Name}  [{row.SafeState}]" + (row.OperatorExclusion is null ? "" : $"  operator excluded: {row.OperatorExclusion.UserPrincipalName}"));
        TypedTenantIdBox.Focus();
    }

    private void OnTypedChanged(object sender, TextChangedEventArgs e) =>
        DeployButton.IsEnabled = string.Equals(TypedTenantId, _expectedTenantId, StringComparison.OrdinalIgnoreCase);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(TypedTenantId, _expectedTenantId, StringComparison.OrdinalIgnoreCase)) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
