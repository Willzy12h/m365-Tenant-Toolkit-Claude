using System.IO;
using System.Windows;
using System.Windows.Threading;
using BDIT.TenantToolkit.App.Services;
using BDIT.TenantToolkit.App.ViewModels;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Configuration;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Engine;

namespace BDIT.TenantToolkit.App;

public partial class App : Application
{
    private Workspace? _workspace;
    private ToolkitLogger? _logger;
    private string? _startupLog;

    public bool Diagnostics { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Diagnostics = e.Args.Any(a => string.Equals(a, "--diagnostics", StringComparison.OrdinalIgnoreCase));
        var rootIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--root", StringComparison.OrdinalIgnoreCase));
        var root = rootIndex >= 0 && rootIndex + 1 < e.Args.Length ? e.Args[rootIndex + 1] : null;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { _logger?.Error("App", "Unobserved task exception.", args.Exception); args.SetObserved(); };

        try
        {
            var paths = ToolkitPaths.Resolve(root);
            paths.EnsureWritableFolders();
            _startupLog = Path.Combine(paths.LogsDirectory, "startup.log");
            StartupNote($"Start {Timestamps.Format(DateTimeOffset.UtcNow)} version {ToolkitVersion.Current} root={paths.Root} diagnostics={Diagnostics}");

            var settings = ToolkitSettings.Load(paths.SettingsFile);
            _logger = new ToolkitLogger(paths.LogsDirectory, Diagnostics ? LogLevel.Debug : ToolkitLogger.ParseLevel(settings.LogLevel));
            _logger.Info("App", $"BDIT Tenant Toolkit {ToolkitVersion.Current} starting (diagnostics={Diagnostics}). Root: {paths.Root}");

            _workspace = new Workspace(paths, settings, _logger, Diagnostics);
            _workspace.Initialise();
            StartupNote("Initialised. Standard: " + (_workspace.Standard?.Release ?? "(none loaded)"));

            var window = new MainWindow { DataContext = new ShellViewModel(_workspace) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            StartupNote("FATAL " + ex.GetType().Name + ": " + ex.Message);
            _logger?.Error("App", "Start-up failed.", ex);
            MessageBox.Show(
                "The BDIT Tenant Toolkit could not start.\n\n" + ex.Message + "\n\n" +
                (_startupLog is null ? "" : "Details: " + _startupLog + "\n") +
                "Run Start-Diagnostics.cmd for verbose logging.",
                "BDIT Tenant Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _workspace?.ShutdownAsync().GetAwaiter().GetResult();
            StartupNote($"Exit {Timestamps.Format(DateTimeOffset.UtcNow)} code {e.ApplicationExitCode}");
        }
        catch (Exception ex)
        {
            _logger?.Error("App", "Shutdown reported an error.", ex);
        }
        finally
        {
            _logger?.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("App", "Unhandled UI exception.", e.Exception);
        MessageBox.Show("An unexpected error occurred. The operation was not completed.\n\n" + SensitiveDataScrubber.Scrub(e.Exception.Message),
            "BDIT Tenant Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        _logger?.Error("App", "Unhandled exception (terminating=" + e.IsTerminating + ").", ex);
        StartupNote("CRASH " + (ex?.GetType().Name ?? "unknown") + ": " + (ex?.Message ?? ""));
        _logger?.Flush();
    }

    private void StartupNote(string line)
    {
        if (_startupLog is null) return;
        try { File.AppendAllText(_startupLog, SensitiveDataScrubber.Scrub(line) + Environment.NewLine); } catch (IOException) { }
    }
}
