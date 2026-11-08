using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AccountUsageUI.App.Presentation;
using AccountUsageUI.App.Usage;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly CreateProfileLoginUseCase _createProfileLogin;
    private readonly ProfileUsageMonitor _usageMonitor;
    private readonly PopupPlacementStore _placementStore;
    private IDisposable? _visibleSurfaceLease;
    private ProfilePopupWindow? _popupWindow;
    private bool _isExiting;

    public MainWindow(
        MainWindowViewModel viewModel,
        CreateProfileLoginUseCase createProfileLogin,
        ProfileUsageMonitor usageMonitor,
        PopupPlacementStore placementStore)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(createProfileLogin);
        ArgumentNullException.ThrowIfNull(usageMonitor);
        ArgumentNullException.ThrowIfNull(placementStore);

        _viewModel = viewModel;
        _createProfileLogin = createProfileLogin;
        _usageMonitor = usageMonitor;
        _placementStore = placementStore;

        InitializeComponent();
        DataContext = _viewModel;

        _usageMonitor.SnapshotChanged += OnUsageSnapshotChanged;
        _usageMonitor.RuntimeStateChanged += OnRuntimeStateChanged;
        _usageMonitor.RefreshingChanged += OnRefreshingChanged;

        IsVisibleChanged += OnWindowIsVisibleChanged;
    }

    public void RestoreFromTray()
    {
        if (_popupWindow is { IsVisible: true })
        {
            ClosePopupAndShowMain();
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void PrepareForExit()
    {
        _isExiting = true;
        _usageMonitor.SnapshotChanged -= OnUsageSnapshotChanged;
        _usageMonitor.RuntimeStateChanged -= OnRuntimeStateChanged;
        _usageMonitor.RefreshingChanged -= OnRefreshingChanged;

        _visibleSurfaceLease?.Dispose();
        _visibleSurfaceLease = null;

        if (_popupWindow is not null)
        {
            _placementStore.SavePopupPosition(_popupWindow.Left, _popupWindow.Top);
            _popupWindow.Close();
            _popupWindow = null;
        }
    }

    private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateMonitorVisibilityLease();
    }

    private void UpdateMonitorVisibilityLease()
    {
        var anyVisible = IsVisible || (_popupWindow is { IsVisible: true });
        if (anyVisible && _visibleSurfaceLease is null && !_isExiting)
        {
            _visibleSurfaceLease = _usageMonitor.AcquireVisibleSurface();
        }
        else if (!anyVisible && _visibleSurfaceLease is not null)
        {
            _visibleSurfaceLease.Dispose();
            _visibleSurfaceLease = null;
        }
    }

    private void OnUsageSnapshotChanged(object? sender, ProfileQuotaSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => _viewModel.ApplyUsageSnapshot(snapshot));
    }

    private void OnRuntimeStateChanged(object? sender, ProfileRuntimeState state)
    {
        Dispatcher.BeginInvoke(
            async () => await _viewModel.ApplyMonitoredRuntimeStateAsync(
                state,
                CancellationToken.None).ConfigureAwait(true));
    }

    private void OnRefreshingChanged(object? sender, bool isRefreshing)
    {
        Dispatcher.BeginInvoke(() => _viewModel.SetUsageRefreshing(isRefreshing));
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshProfilesAsync(CancellationToken.None).ConfigureAwait(true);
        await _usageMonitor.RefreshAllNowAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        var catalog = _viewModel.GetAllDiscoveredModelNames();
        var settingsVm = new SettingsViewModel(catalog, _viewModel.FavoriteModels);
        var window = new SettingsWindow(
            settingsVm,
            selected => _viewModel.UpdateFavoriteModelsAsync(selected, CancellationToken.None))
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private async void OnAddProfileClicked(object sender, RoutedEventArgs e)
    {
        using var pause = await _usageMonitor.PauseAsync(CancellationToken.None).ConfigureAwait(true);
        var window = new NewProfileWindow(_createProfileLogin)
        {
            Owner = this
        };

        window.ShowDialog();
        await _viewModel.RefreshProfilesAsync(CancellationToken.None).ConfigureAwait(true);
        await _usageMonitor.RefreshAllNowAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async void OnPrimaryProfileActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileListItemViewModel item })
        {
            return;
        }

        await ExecuteSwitchOrRunAsync(item).ConfigureAwait(true);
    }

    private async Task ExecuteSwitchOrRunAsync(ProfileListItemViewModel item)
    {
        using var pause = await _usageMonitor.PauseAsync(CancellationToken.None).ConfigureAwait(true);
        if (item.IsSwitchAction)
        {
            await _viewModel.SwitchProfileAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            await _viewModel.RunProfileAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnMoreMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileListItemViewModel item } button)
        {
            return;
        }

        var menu = new ContextMenu();
        var refreshItem = new MenuItem
        {
            Header = "사용량 새로고침",
            FontWeight = FontWeights.SemiBold
        };
        refreshItem.Click += async (_, _) =>
            await _usageMonitor.RefreshAllNowAsync(CancellationToken.None).ConfigureAwait(true);

        var deleteItem = new MenuItem
        {
            Header = "프로필 삭제...",
            FontWeight = FontWeights.SemiBold,
            IsEnabled = item.IsDeleteEnabled,
            Foreground = System.Windows.Media.Brushes.Firebrick
        };
        deleteItem.Click += async (_, _) => await PromptDeleteProfileAsync(item).ConfigureAwait(true);

        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async Task PromptDeleteProfileAsync(ProfileListItemViewModel item)
    {
        var confirmWindow = new DeleteProfileConfirmationWindow(item.Name)
        {
            Owner = this
        };

        if (confirmWindow.ShowDialog() == true)
        {
            using var pause = await _usageMonitor.PauseAsync(CancellationToken.None).ConfigureAwait(true);
            await _viewModel.DeleteProfileAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnOpenPopupClicked(object sender, RoutedEventArgs e)
    {
        if (_popupWindow is null)
        {
            _popupWindow = new ProfilePopupWindow(
                _viewModel,
                onSwitchProfileAsync: ExecuteSwitchOrRunAsync,
                onReturnToMain: ClosePopupAndShowMain);

            if (_placementStore.TryLoadPopupPosition(out var left, out var top))
            {
                _popupWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _popupWindow.Left = left;
                _popupWindow.Top = top;
            }
            else
            {
                var workArea = SystemParameters.WorkArea;
                _popupWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _popupWindow.Left = Math.Max(workArea.Left + 16, workArea.Right - 296);
                _popupWindow.Top = workArea.Top + 24;
            }

            _popupWindow.IsVisibleChanged += (_, _) => UpdateMonitorVisibilityLease();
            _popupWindow.Closed += (_, _) =>
            {
                _popupWindow = null;
                UpdateMonitorVisibilityLease();
            };
        }

        _popupWindow.Show();
        _popupWindow.Activate();
        Hide();
        UpdateMonitorVisibilityLease();
    }

    private void ClosePopupAndShowMain()
    {
        if (_popupWindow is not null)
        {
            _placementStore.SavePopupPosition(_popupWindow.Left, _popupWindow.Top);
            _popupWindow.Close();
            _popupWindow = null;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        UpdateMonitorVisibilityLease();
    }

    private void OnMinimizeWindowClicked(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeWindowClicked(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseWindowClicked(object sender, RoutedEventArgs e)
    {
        Hide();
        UpdateMonitorVisibilityLease();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            UpdateMonitorVisibilityLease();
        }
    }
}
