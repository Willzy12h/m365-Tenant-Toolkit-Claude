using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using BDIT.TenantToolkit.App.Infrastructure;
using BDIT.TenantToolkit.App.Services;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Models;

namespace BDIT.TenantToolkit.App.ViewModels;

public sealed class NavItem : ObservableObject
{
    private bool _isActive;
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Step { get; init; } = "";
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

/// <summary>Base for every page: shared access to the workspace and a consistent way to run commands and surface errors.</summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(ShellViewModel shell, string title)
    {
        Shell = shell;
        Title = title;
        shell.Workspace.StateChanged += Refresh;
    }

    public ShellViewModel Shell { get; }
    public Workspace Workspace => Shell.Workspace;
    public string Title { get; }

    /// <summary>Copies the command parameter (or the supplied text) to the clipboard. Used by every Copy button.</summary>
    protected RelayCommand CopyText(Func<string> text) => new(() => Shell.CopyToClipboard(text()));

    protected AsyncCommand Command(Func<Task> execute, Func<bool>? canExecute = null) => new(execute, Shell.ShowError, canExecute);
    protected AsyncCommand Command<T>(Func<T?, Task> execute, Func<bool>? canExecute = null) => new(p => execute(p is T t ? t : default), Shell.ShowError, canExecute is null ? null : _ => canExecute());
    protected RelayCommand Sync(Action execute, Func<bool>? canExecute = null) => new(() => { try { execute(); } catch (Exception ex) { Shell.ShowError(ex); } }, canExecute);

    /// <summary>Called whenever workspace state changes. Implementations must be cheap and idempotent.</summary>
    public abstract void Refresh();

    public virtual void OnNavigatedTo() => Refresh();
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> _pages = new(StringComparer.Ordinal);
    private object? _currentPage;
    private string _errorMessage = "";

    public Workspace Workspace { get; }
    public ObservableCollection<NavItem> NavItems { get; } = new();
    public ICommand NavigateCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ClearErrorCommand { get; }
    /// <summary>Copies its command parameter to the clipboard; bound from Copy buttons across the pages.</summary>
    public ICommand CopyCommand { get; }
    public ICommand CopyDetailsCommand { get; }

    public ShellViewModel(Workspace workspace)
    {
        Workspace = workspace;
        NavItems.Add(new NavItem { Key = "connect", Step = "1", Title = "Connect" });
        NavItems.Add(new NavItem { Key = "configuration", Step = "2", Title = "Configuration" });
        NavItems.Add(new NavItem { Key = "assessment", Step = "3", Title = "Assessment" });
        NavItems.Add(new NavItem { Key = "deviations", Step = "4", Title = "Deviations" });
        NavItems.Add(new NavItem { Key = "plan", Step = "5", Title = "Plan changes" });
        NavItems.Add(new NavItem { Key = "deploy", Step = "6", Title = "Deploy" });
        NavItems.Add(new NavItem { Key = "history", Step = "", Title = "Evidence and drift" });
        NavItems.Add(new NavItem { Key = "checks", Step = "", Title = "Manual checks" });
        NavItems.Add(new NavItem { Key = "standard", Step = "", Title = "Build Standard" });
        NavItems.Add(new NavItem { Key = "settings", Step = "", Title = "Settings and diagnostics" });

        _pages["connect"] = new ConnectViewModel(this);
        _pages["configuration"] = new ConfigurationViewModel(this);
        _pages["assessment"] = new AssessmentViewModel(this);
        _pages["deviations"] = new DeviationsViewModel(this);
        _pages["plan"] = new PlanViewModel(this);
        _pages["deploy"] = new DeployViewModel(this);
        _pages["history"] = new HistoryViewModel(this);
        _pages["checks"] = new ManualChecksViewModel(this);
        _pages["standard"] = new StandardViewModel(this);
        _pages["settings"] = new SettingsViewModel(this);

        NavigateCommand = new RelayCommand(p => { if (p is NavItem item) Navigate(item.Key); });
        CopyCommand = new RelayCommand(p => CopyToClipboard(p as string ?? p?.ToString() ?? ""));
        CopyDetailsCommand = new RelayCommand(() => CopyToClipboard(DetailsText));
        DisconnectCommand = new AsyncCommand(Workspace.DisconnectAsync, ShowError, () => Workspace.IsConnected && Workspace.Idle);
        ClearErrorCommand = new RelayCommand(() => ErrorMessage = "");

        workspace.StateChanged += () => RaiseHeader();
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Workspace.Busy) or nameof(Workspace.BusyMessage) or nameof(Workspace.ProgressDetail))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyMessage));
                OnPropertyChanged(nameof(ProgressDetail));
            }
        };
        if (workspace.StandardError is not null) ErrorMessage = workspace.StandardError;
        Navigate("connect");
    }

    public object? CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }

    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        foreach (var item in NavItems) item.IsActive = item.Key == key;
        CurrentPage = page;
        page.OnNavigatedTo();
    }

    public T Page<T>() where T : PageViewModel => _pages.Values.OfType<T>().First();

    public ObservableCollection<LogEntry> Activity => Workspace.Activity;

    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>
    /// Clipboard writes can fail transiently when another process holds the clipboard; that must never take the window
    /// down or raise an error bar over a convenience action.
    /// </summary>
    public void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            Workspace.Logger.Debug("UI", $"Copied {text.Length} character(s) to the clipboard.");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Workspace.Logger.Warn("UI", "The clipboard was unavailable: " + ex.Message);
        }
    }

    public void ShowError(Exception ex)
    {
        ErrorMessage = SensitiveDataScrubber.Scrub(ex.Message);
        Workspace.Logger.Error("UI", ex.Message, ex, Workspace.Profile?.TenantId);
    }

    public bool IsBusy => Workspace.Busy;
    public string BusyMessage => Workspace.BusyMessage;
    public string ProgressDetail => Workspace.ProgressDetail;

    public string ProductName => Workspace.Settings.ProductName.ToUpperInvariant();
    public string WindowTitle => $"{Workspace.Settings.ProductName} {Workspace.Version}";
    public bool IsConnected => Workspace.IsConnected;
    public bool IsDeploymentSession => Workspace.Session?.Mode == SessionMode.Deployment;
    public bool IsAssessmentSession => Workspace.Session?.Mode == SessionMode.Assessment;
    public string ModeBadge => Workspace.Session is null ? "NOT CONNECTED" : Workspace.Session.Mode == SessionMode.Deployment ? "DEPLOYMENT ACCESS - WRITES POSSIBLE" : "READ-ONLY ASSESSMENT";
    public string TenantTitle => Workspace.Session?.TenantName is { Length: > 0 } name ? name : Workspace.Profile?.Company ?? "No client selected";
    public string TenantSubtitle => Workspace.Session is not null
        ? (Workspace.Session.PrimaryDomain.Length > 0 ? Workspace.Session.PrimaryDomain : Workspace.Session.TenantId) + (Workspace.Session.TenantVerified ? " · verified" : " · NOT verified")
        : Workspace.Profile?.TenantId ?? "Choose or create a client on the Connect page";
    public string StandardText => Workspace.Standard is null ? "No Build Standard loaded" : $"Build Standard {Workspace.Standard.Release} · {Workspace.Standard.IntegrityDigest[..Math.Min(12, Workspace.Standard.IntegrityDigest.Length)]}";
    public string VersionText => $"{Workspace.Settings.ProductName} {Workspace.Version} · Evidence: {Workspace.Paths.DataDirectory}";

    public string DetailsText
    {
        get
        {
            var s = Workspace.Session;
            if (s is null) return "Not connected. A saved profile is not an authenticated connection.";
            var sb = new StringBuilder();
            sb.AppendLine("Tenant ID:      " + s.TenantId);
            sb.AppendLine("Account:        " + s.Account);
            sb.AppendLine("Operator ID:    " + (s.OperatorObjectId ?? "not resolved") + (s.OperatorVerified ? " (verified)" : " (NOT verified)"));
            sb.AppendLine("Application:    " + s.ClientLabel);
            sb.AppendLine("Client ID:      " + s.ClientId);
            sb.AppendLine("Authentication: " + s.AuthenticationType);
            sb.AppendLine("Mode:           " + s.Mode);
            sb.AppendLine("Scopes:         " + string.Join(", ", s.Scopes));
            sb.AppendLine("Connected:      " + s.ConnectedAt);
            foreach (var n in s.Notices) sb.AppendLine("Notice:         " + n);
            return sb.ToString().TrimEnd();
        }
    }

    private void RaiseHeader()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsDeploymentSession));
        OnPropertyChanged(nameof(IsAssessmentSession));
        OnPropertyChanged(nameof(ModeBadge));
        OnPropertyChanged(nameof(TenantTitle));
        OnPropertyChanged(nameof(TenantSubtitle));
        OnPropertyChanged(nameof(StandardText));
        OnPropertyChanged(nameof(DetailsText));
        OnPropertyChanged(nameof(IsBusy));
        if (Workspace.StandardError is not null && ErrorMessage.Length == 0) ErrorMessage = Workspace.StandardError;
    }
}
