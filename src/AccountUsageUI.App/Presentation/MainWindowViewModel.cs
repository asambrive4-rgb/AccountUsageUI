using System.Collections.ObjectModel;
using AccountUsageUI.Core.Installation;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App.Presentation;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DetectAppInstallationUseCase _detectInstallation;
    private readonly ListProfilesUseCase _listProfiles;
    private readonly CreateProfileLoginUseCase _profileLogin;
    private readonly RunProfileUseCase _runProfile;
    private readonly SwitchProfileUseCase _switchProfile;
    private readonly DeleteProfileUseCase _deleteProfile;
    private readonly GetProfileRuntimeStateUseCase _getRuntimeState;
    private readonly IUserSettingsStore _settingsStore;
    private readonly ProfileListPresentationState _profileList = new();
    private readonly SemaphoreSlim _runtimeRefreshGate = new(1, 1);
    private ProfileRuntimeState? _lastRuntimeState;
    private bool? _lastRuntimeCanOperate;
    private string _installationStatusMessage = "확인 중";
    private string _profileStatusMessage = "프로필을 불러오는 중...";
    private string _runtimeStatusMessage = "Antigravity 상태 확인 중...";
    private string _operationStatusMessage = "";
    private bool _hasPendingRecovery;
    private bool _isOperationInProgress;
    private bool _isUsageRefreshing;
    private bool _isRuntimePresentationDirty = true;
    private AppSettings _currentSettings = AppSettings.Default;

    public MainWindowViewModel(
        DetectAppInstallationUseCase detectInstallation,
        ListProfilesUseCase listProfiles,
        CreateProfileLoginUseCase profileLogin,
        RunProfileUseCase runProfile,
        SwitchProfileUseCase switchProfile,
        DeleteProfileUseCase deleteProfile,
        GetProfileRuntimeStateUseCase getRuntimeState,
        IUserSettingsStore settingsStore)
    {
        _detectInstallation = detectInstallation;
        _listProfiles = listProfiles;
        _profileLogin = profileLogin;
        _runProfile = runProfile;
        _switchProfile = switchProfile;
        _deleteProfile = deleteProfile;
        _getRuntimeState = getRuntimeState;
        _settingsStore = settingsStore;
    }

    public ObservableCollection<ProfileListItemViewModel> Profiles =>
        _profileList.Profiles;

    public ReadOnlyObservableCollection<ProfileListItemViewModel>
        InactiveProfiles =>
        _profileList.InactiveProfiles;

    public ProfileListItemViewModel? ActiveProfile =>
        _profileList.ActiveProfile;

    public ProfileListItemViewModel? DefaultPopupProfile =>
        _profileList.DefaultPopupProfile;

    public bool HasActiveProfile => ActiveProfile is not null;

    public bool HasNoProfiles => Profiles.Count == 0;

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasSingleProfile => Profiles.Count == 1;

    public bool HasInactiveProfiles => InactiveProfiles.Count > 0;

    public IReadOnlyList<string> FavoriteModels =>
        _currentSettings.FavoriteModels;

    public string InstallationStatusMessage
    {
        get => _installationStatusMessage;
        private set => SetField(ref _installationStatusMessage, value);
    }

    public string ProfileStatusMessage
    {
        get => _profileStatusMessage;
        private set => SetField(ref _profileStatusMessage, value);
    }

    public string RuntimeStatusMessage
    {
        get => _runtimeStatusMessage;
        private set => SetField(ref _runtimeStatusMessage, value);
    }

    public string OperationStatusMessage
    {
        get => _operationStatusMessage;
        private set
        {
            if (SetField(ref _operationStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasOperationStatusMessage));
            }
        }
    }

    public bool HasOperationStatusMessage =>
        !string.IsNullOrWhiteSpace(OperationStatusMessage);

    public bool HasPendingRecovery
    {
        get => _hasPendingRecovery;
        private set
        {
            if (SetField(ref _hasPendingRecovery, value))
            {
                OnPropertyChanged(nameof(CanAddProfile));
            }
        }
    }

    public bool CanAddProfile =>
        !_isOperationInProgress &&
        !HasPendingRecovery;

    public bool IsOperationInProgress => _isOperationInProgress;

    public bool IsUsageRefreshing
    {
        get => _isUsageRefreshing;
        private set
        {
            if (SetField(ref _isUsageRefreshing, value))
            {
                OnPropertyChanged(nameof(CanRefreshUsage));
            }
        }
    }

    public bool CanRefreshUsage => !IsUsageRefreshing;

    public void SetUsageRefreshing(bool value) =>
        IsUsageRefreshing = value;

    public void ApplyUsageSnapshot(ProfileQuotaSnapshot snapshot) =>
        _profileList.ApplyUsageSnapshot(snapshot);

    public IReadOnlyList<string> GetAllDiscoveredModelNames()
    {
        var set = new HashSet<string>(
            FavoriteModelsPolicy.CatalogModels,
            StringComparer.OrdinalIgnoreCase);

        foreach (var profile in Profiles)
        {
            foreach (var name in profile.GetDiscoveredModelFamilyNames())
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    set.Add(name);
                }
            }
        }

        foreach (var fav in _currentSettings.FavoriteModels)
        {
            if (!string.IsNullOrWhiteSpace(fav))
            {
                set.Add(fav);
            }
        }

        return set.ToArray();
    }

    public async Task UpdateFavoriteModelsAsync(
        IReadOnlyList<string> favoriteModels,
        CancellationToken cancellationToken)
    {
        var updated = new AppSettings(favoriteModels);
        _currentSettings = updated;
        _profileList.ApplyFavoriteFilters(updated.FavoriteModels);
        OnPropertyChanged(nameof(FavoriteModels));
        await _settingsStore.SaveAsync(updated, cancellationToken);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        await InitializeShellAsync(cancellationToken);
        await RefreshRuntimeStateAsync(
            cancellationToken,
            forceApply: true,
            forceRuntimeRefresh: true);
    }

    public async Task InitializeShellAsync(
        CancellationToken cancellationToken)
    {
        var shell = await RunOffUiThreadAsync(
            async token =>
            {
                var settings = await _settingsStore.LoadAsync(token);
                var installation =
                    await _detectInstallation.ExecuteAsync(token);
                var profiles =
                    await _listProfiles.ExecuteAsync(token);
                var hasRecovery =
                    await _profileLogin.HasPendingRecoveryAsync(token);
                return new ShellLoadResult(
                    settings,
                    installation,
                    profiles,
                    hasRecovery);
            },
            cancellationToken);

        _currentSettings = shell.Settings;
        _profileList.ApplyFavoriteFilters(shell.Settings.FavoriteModels);

        InstallationStatusMessage = shell.Installation.Status switch
        {
            AppInstallationDetectionStatus.Installed =>
                "Antigravity 설치됨",
            AppInstallationDetectionStatus.NotInstalled =>
                "Antigravity 미설치",
            _ => "설치 확인 오류"
        };

        _profileList.Replace(shell.Profiles.Profiles);
        InvalidateRuntimePresentation();
        ProfileStatusMessage = shell.Profiles.Issues.Count > 0
            ? "일부 프로필 저장 데이터를 읽지 못했습니다."
            : shell.Profiles.Profiles.Count == 0
                ? "저장된 프로필이 없습니다."
                : $"프로필 {shell.Profiles.Profiles.Count}개";
        HasPendingRecovery = shell.HasPendingRecovery;
        if (shell.HasPendingRecovery)
        {
            _profileList.DisableAllActions();
        }

        NotifyProfileCollectionsChanged();
    }

    public async Task RefreshProfilesAsync(
        CancellationToken cancellationToken)
    {
        var result = await RunOffUiThreadAsync(
            token => _listProfiles.ExecuteAsync(token),
            cancellationToken);
        _profileList.Replace(result.Profiles);
        InvalidateRuntimePresentation();
        ProfileStatusMessage = result.Issues.Count > 0
            ? "일부 프로필 저장 데이터를 읽지 못했습니다."
            : result.Profiles.Count == 0
                ? "저장된 프로필이 없습니다."
                : $"프로필 {result.Profiles.Count}개";

        await RefreshRuntimeStateAsync(
            cancellationToken,
            forceApply: true,
            forceRuntimeRefresh: true);
        NotifyProfileCollectionsChanged();
    }

    public Task RefreshRuntimeStateAsync(
        CancellationToken cancellationToken) =>
        RefreshRuntimeStateAsync(
            cancellationToken,
            forceApply: false,
            forceRuntimeRefresh: false);

    public Task ApplyMonitoredRuntimeStateAsync(
        ProfileRuntimeState state,
        CancellationToken cancellationToken) =>
        ApplyRuntimeRefreshAsync(
            async token => new RuntimeRefreshResult(
                await _profileLogin.HasPendingRecoveryAsync(token),
                state),
            forceApply: false,
            cancellationToken);

    private async Task RefreshRuntimeStateAsync(
        CancellationToken cancellationToken,
        bool forceApply,
        bool forceRuntimeRefresh)
    {
        await ApplyRuntimeRefreshAsync(
            async token => new RuntimeRefreshResult(
                await _profileLogin.HasPendingRecoveryAsync(token),
                await _getRuntimeState.ExecuteAsync(
                    forceRuntimeRefresh,
                    token)),
            forceApply,
            cancellationToken);
    }

    private async Task ApplyRuntimeRefreshAsync(
        Func<CancellationToken, Task<RuntimeRefreshResult>> load,
        bool forceApply,
        CancellationToken cancellationToken)
    {
        if (_isOperationInProgress ||
            !await _runtimeRefreshGate.WaitAsync(
                millisecondsTimeout: 0,
                cancellationToken))
        {
            return;
        }

        try
        {
            var result = await RunOffUiThreadAsync(
                load,
                cancellationToken);

            HasPendingRecovery = result.HasPendingRecovery;
            ApplyRuntimePresentation(
                result.State,
                forceApply);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            RuntimeStatusMessage =
                "Antigravity 상태를 확인하지 못했습니다.";
            _profileList.DisableAllActions();
            InvalidateRuntimePresentation();
        }
        finally
        {
            _runtimeRefreshGate.Release();
        }
    }

    public Task<RunProfileResult> RunProfileAsync(
        ProfileId profileId,
        CancellationToken cancellationToken) =>
        ExecuteProfileOperationAsync(
            profileId,
            "실행 중...",
            "선택한 프로필로 Antigravity 실행 중...",
            new RunProfileResult(RunProfileStatus.Failed),
            _runProfile.ExecuteAsync,
            result =>
                ProfileOperationMessageFormatter.Describe(result.Status),
            (_, token) => RefreshRuntimeStateAfterChangeAsync(token),
            cancellationToken);

    public Task<SwitchProfileResult> SwitchProfileAsync(
        ProfileId profileId,
        CancellationToken cancellationToken) =>
        ExecuteProfileOperationAsync(
            profileId,
            "전환 중...",
            "선택한 프로필로 Antigravity 전환 중...",
            new SwitchProfileResult(SwitchProfileStatus.Failed),
            _switchProfile.ExecuteAsync,
            result =>
                ProfileOperationMessageFormatter.Describe(result.Status),
            (_, token) => RefreshRuntimeStateAfterChangeAsync(token),
            cancellationToken);

    public IReadOnlyList<ProfileListItemViewModel> GetOverlaySwitchCandidates() =>
        Profiles
            .Where(profile => !profile.IsActive && profile.IsRunEnabled)
            .ToArray();

    public bool CanOverlaySwitch =>
        GetOverlaySwitchCandidates().Count > 0;

    public Task<DeleteProfileResult> DeleteProfileAsync(
        ProfileId profileId,
        CancellationToken cancellationToken) =>
        ExecuteProfileOperationAsync(
            profileId,
            "삭제 중...",
            "선택한 프로필 삭제 중...",
            new DeleteProfileResult(DeleteProfileStatus.Failed),
            _deleteProfile.ExecuteAsync,
            result =>
                ProfileOperationMessageFormatter.Describe(result.Status),
            (result, token) =>
                result.Status == DeleteProfileStatus.Deleted
                    ? RefreshProfilesAsync(token)
                    : RefreshRuntimeStateAfterChangeAsync(token),
            cancellationToken);

    private bool CanOperateOnProfiles =>
        !HasPendingRecovery &&
        !_isOperationInProgress;

    private Task RefreshRuntimeStateAfterChangeAsync(
        CancellationToken cancellationToken) =>
        RefreshRuntimeStateAsync(
            cancellationToken,
            forceApply: true,
            forceRuntimeRefresh: true);

    private async Task<TResult> ExecuteProfileOperationAsync<TResult>(
        ProfileId profileId,
        string itemStatus,
        string operationStatus,
        TResult busyResult,
        Func<ProfileId, CancellationToken, Task<TResult>> execute,
        Func<TResult, string> describeResult,
        Func<TResult, CancellationToken, Task> refreshAfter,
        CancellationToken cancellationToken)
    {
        if (_isOperationInProgress)
        {
            return busyResult;
        }

        SetOperationInProgress(true);
        _profileList.BeginOperation(profileId, itemStatus);
        InvalidateRuntimePresentation();
        NotifyProfileCollectionsChanged();
        OperationStatusMessage = operationStatus;

        TResult result;
        try
        {
            result = await RunOffUiThreadAsync(
                token => execute(profileId, token),
                cancellationToken);
            OperationStatusMessage = describeResult(result);
        }
        finally
        {
            SetOperationInProgress(false);
        }

        HasPendingRecovery =
            await RunOffUiThreadAsync(
                token => _profileLogin.HasPendingRecoveryAsync(token),
                cancellationToken);
        await refreshAfter(result, cancellationToken);
        NotifyProfileCollectionsChanged();
        return result;
    }

    private void ApplyRuntimePresentation(
        ProfileRuntimeState state,
        bool forceApply)
    {
        var canOperate = CanOperateOnProfiles;
        if (!forceApply &&
            !_isRuntimePresentationDirty &&
            _lastRuntimeCanOperate == canOperate &&
            EqualityComparer<ProfileRuntimeState>.Default.Equals(
                _lastRuntimeState,
                state))
        {
            return;
        }

        var previousActiveProfileId = ActiveProfile?.Id;
        RuntimeStatusMessage = _profileList.ApplyRuntimeState(
            state,
            canOperate);
        _lastRuntimeState = state;
        _lastRuntimeCanOperate = canOperate;
        _isRuntimePresentationDirty = false;

        if (forceApply ||
            previousActiveProfileId != ActiveProfile?.Id)
        {
            NotifyProfileCollectionsChanged();
        }
    }

    private void InvalidateRuntimePresentation()
    {
        _isRuntimePresentationDirty = true;
    }

    private static async Task<TResult> RunOffUiThreadAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
                () => action(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void NotifyProfileCollectionsChanged()
    {
        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(HasActiveProfile));
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasSingleProfile));
        OnPropertyChanged(nameof(HasInactiveProfiles));
        OnPropertyChanged(nameof(DefaultPopupProfile));
        OnPropertyChanged(nameof(CanOverlaySwitch));
    }

    private void SetOperationInProgress(bool value)
    {
        if (_isOperationInProgress == value)
        {
            return;
        }

        _isOperationInProgress = value;
        OnPropertyChanged(nameof(CanAddProfile));
        OnPropertyChanged(nameof(IsOperationInProgress));
    }

    private sealed record RuntimeRefreshResult(
        bool HasPendingRecovery,
        ProfileRuntimeState State);

    private sealed record ShellLoadResult(
        AppSettings Settings,
        DetectAppInstallationResult Installation,
        ProfileStoreReadResult Profiles,
        bool HasPendingRecovery);
}
