using System.ComponentModel;
using System.Windows;
using BDIT.TenantToolkit.App.ViewModels;

namespace BDIT.TenantToolkit.App;

public partial class MainWindow : Window
{
    private bool _closeApproved;

    public MainWindow() => InitializeComponent();

    /// <summary>Never abandons an in-flight tenant write: closing waits for the executor to reach a safe boundary and finish evidence.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        if (DataContext is not ShellViewModel shell) return;
        if (!shell.Workspace.Executor.IsRunning) return;

        e.Cancel = true;
        var result = MessageBox.Show(
            "A deployment is in progress.\n\nThe toolkit will stop at the next safe boundary, finish the current write, capture the after-change snapshot and then close. Nothing is abandoned mid-write.\n\nStop and close?",
            "Deployment in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        IsEnabled = false;
        Title = shell.WindowTitle + " - finishing evidence before closing";
        await shell.Workspace.Executor.WaitForCompletionAsync(shell.Workspace.Control);
        _closeApproved = true;
        Close();
    }
}
