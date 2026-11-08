using System.Windows;
using AccountUsageUI.App.Presentation;
using AccountUsageUI.App.Usage;
using AccountUsageUI.Core.Installation;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;
using AccountUsageUI.Infrastructure.Profiles;
using AccountUsageUI.Infrastructure.Providers.Antigravity;
using AccountUsageUI.Infrastructure.Usage;

namespace AccountUsageUI.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private TrayIconService? _trayIconService;
    private ProfileUsageMonitor? _usageMonitor;
    private MainWindow? _mainWindow;

    private static void LogCrash(string category, object? exception)
    {
        try
        {
            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccountUsageUI");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {category}: {exception}\n");
        }
        catch
        {
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("AppDomain Unhandled", args.ExceptionObject);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("Dispatcher Unhandled", args.Exception);
        };

        _singleInstanceCoordinator = new SingleInstanceCoordinator();
        if (!_singleInstanceCoordinator.IsPrimaryInstance)
        {
            _singleInstanceCoordinator.SignalPrimaryInstance();
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var installationLocator = new AntigravityInstallationLocator();
        var detectInstallation = new DetectAppInstallationUseCase(installationLocator);

        var profileStore = new WindowsProfileStore();
        var authSession = new AntigravityAuthenticationSession();
        var processController = new AntigravityProcessController(installationLocator);
        var identityReader = new AntigravityCredentialIdentityReader();
        var operationCoordinator = new ProfileOperationCoordinator();

        var listProfiles = new ListProfilesUseCase(profileStore);
        var createProfileLogin = new CreateProfileLoginUseCase(
            profileStore,
            authSession,
            processController,
            operationCoordinator);
        var runProfile = new RunProfileUseCase(
            profileStore,
            authSession,
            processController,
            operationCoordinator,
            identityReader);
        var switchProfile = new SwitchProfileUseCase(
            profileStore,
            authSession,
            processController,
            operationCoordinator,
            identityReader);
        var deleteProfile = new DeleteProfileUseCase(
            profileStore,
            authSession,
            processController,
            operationCoordinator,
            identityReader);
        var getRuntimeState = new GetProfileRuntimeStateUseCase(
            profileStore,
            authSession,
            processController,
            identityReader);

        var quotaReader = new AntigravityQuotaReader();
        var snapshotCache = new FileProfileUsageSnapshotCache();
        var refreshQuota = new RefreshProfileQuotaUseCase(
            profileStore,
            quotaReader,
            operationCoordinator);

        var settingsStore = new JsonUserSettingsStore();
        _usageMonitor = new ProfileUsageMonitor(
            listProfiles,
            getRuntimeState,
            refreshQuota,
            quotaReader,
            snapshotCache);

        var viewModel = new MainWindowViewModel(
            detectInstallation,
            listProfiles,
            createProfileLogin,
            runProfile,
            switchProfile,
            deleteProfile,
            getRuntimeState,
            settingsStore);

        var placementStore = new PopupPlacementStore();
        _mainWindow = new MainWindow(
            viewModel,
            createProfileLogin,
            _usageMonitor,
            placementStore);
        MainWindow = _mainWindow;

        _trayIconService = new TrayIconService(
            onOpenRequested: () => Dispatcher.BeginInvoke(() => _mainWindow?.RestoreFromTray()),
            onExitRequested: () => Dispatcher.BeginInvoke(RequestExit));

        _singleInstanceCoordinator.StartListening(
            () => Dispatcher.BeginInvoke(() => _mainWindow?.RestoreFromTray()));

        _mainWindow.Show();
        _mainWindow.Activate();

        try
        {
            await viewModel.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogCrash("viewModel.InitializeAsync", ex);
        }
    }

    public void RequestExit()
    {
        _mainWindow?.PrepareForExit();
        _usageMonitor?.Dispose();
        _trayIconService?.Dispose();
        _singleInstanceCoordinator?.Dispose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _usageMonitor?.Dispose();
        _trayIconService?.Dispose();
        _singleInstanceCoordinator?.Dispose();
        base.OnExit(e);
    }
}
