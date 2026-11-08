using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App.Usage;

public sealed class ProfileUsageMonitor : IDisposable
{
    public static readonly TimeSpan MonitorInterval =
        TimeSpan.FromSeconds(4);

    private readonly ListProfilesUseCase _listProfiles;
    private readonly GetProfileRuntimeStateUseCase _getRuntimeState;
    private readonly RefreshProfileQuotaUseCase _refreshProfile;
    private readonly IProfileQuotaReader _reader;
    private readonly IProfileUsageSnapshotCache _snapshotCache;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<ProfileId, ProfileQuotaSnapshot>
        _snapshots = [];
    private readonly Dictionary<ProfileId, ProfileQuotaSnapshot>
        _publishedSnapshots = [];
    private readonly Dictionary<ProfileId, DateTimeOffset>
        _lastAttempts = [];
    private readonly Dictionary<ProfileId, DateTimeOffset>
        _cacheCapturedAt = [];
    private readonly Dictionary<ProfileId, int> _consecutiveFailures = [];
    private CancellationTokenSource? _monitorCancellation;
    private ProfileRuntimeState? _publishedRuntimeState;
    private int _visibleSurfaceCount;
    private int _pauseCount;
    private bool _disposed;
    private bool _cacheLoadStarted;

    public ProfileUsageMonitor(
        ListProfilesUseCase listProfiles,
        GetProfileRuntimeStateUseCase getRuntimeState,
        RefreshProfileQuotaUseCase refreshProfile,
        IProfileQuotaReader reader,
        IProfileUsageSnapshotCache snapshotCache)
    {
        _listProfiles = listProfiles;
        _getRuntimeState = getRuntimeState;
        _refreshProfile = refreshProfile;
        _reader = reader;
        _snapshotCache = snapshotCache;
    }

    public event EventHandler<ProfileQuotaSnapshot>? SnapshotChanged;

    public event EventHandler<ProfileRuntimeState>? RuntimeStateChanged;

    public event EventHandler<bool>? RefreshingChanged;

    public IDisposable AcquireVisibleSurface()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateGate)
        {
            _visibleSurfaceCount++;
            if (_visibleSurfaceCount == 1)
            {
                StartMonitor();
            }
        }

        return new Lease(ReleaseVisibleSurface);
    }

    public async Task<IDisposable> PauseAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            _pauseCount++;
        }

        try
        {
            await _refreshGate.WaitAsync(cancellationToken);
            _refreshGate.Release();
            return new Lease(Resume);
        }
        catch
        {
            Resume();
            throw;
        }
    }

    public Task RefreshAllNowAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RefreshCycleAsync(
            UsageRefreshKind.ManualForce,
            forceRuntimeRefresh: true,
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? cancellation;
        lock (_stateGate)
        {
            cancellation = _monitorCancellation;
            _monitorCancellation = null;
            _visibleSurfaceCount = 0;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        _reader.Dispose();
    }

    private void StartMonitor()
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = new CancellationTokenSource();
        _ = MonitorAsync(_monitorCancellation.Token);
    }

    private void ReleaseVisibleSurface()
    {
        CancellationTokenSource? cancellation = null;
        lock (_stateGate)
        {
            if (_visibleSurfaceCount == 0)
            {
                return;
            }

            _visibleSurfaceCount--;
            if (_visibleSurfaceCount == 0)
            {
                cancellation = _monitorCancellation;
                _monitorCancellation = null;
            }
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        if (cancellation is not null)
        {
            _ = Task.Run(_reader.StopActiveSession);
        }
    }

    private void Resume()
    {
        var shouldRefresh = false;
        lock (_stateGate)
        {
            if (_pauseCount > 0)
            {
                _pauseCount--;
            }

            shouldRefresh =
                _pauseCount == 0 &&
                _visibleSurfaceCount > 0;
        }

        if (shouldRefresh)
        {
            _ = RefreshAllNowAsync(CancellationToken.None);
        }
    }

    private async Task MonitorAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await EnsureCacheLoadedAsync(cancellationToken);
                await RefreshCycleAsync(
                    UsageRefreshKind.OpenSurface,
                    forceRuntimeRefresh: true,
                    cancellationToken);
            }
            catch (Exception)
                when (!cancellationToken.IsCancellationRequested)
            {
            }

            using var timer = new PeriodicTimer(MonitorInterval);
            while (await timer.WaitForNextTickAsync(
                       cancellationToken))
            {
                try
                {
                    await RefreshCycleAsync(
                        UsageRefreshKind.Scheduled,
                        forceRuntimeRefresh: false,
                        cancellationToken);
                }
                catch (Exception)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureCacheLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (_cacheLoadStarted)
        {
            return;
        }

        _cacheLoadStarted = true;
        try
        {
            await _snapshotCache.LoadAsync(cancellationToken);
        }
        catch (Exception)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCycleAsync(
        UsageRefreshKind kind,
        bool forceRuntimeRefresh,
        CancellationToken cancellationToken)
    {
        if (!ShouldRun() ||
            !await _refreshGate.WaitAsync(
                millisecondsTimeout: 0,
                cancellationToken))
        {
            return;
        }

        var ignoreCache =
            kind == UsageRefreshKind.ManualForce;
        var showRefreshing =
            kind is UsageRefreshKind.ManualForce
                or UsageRefreshKind.OpenSurface;

        if (showRefreshing)
        {
            RefreshingChanged?.Invoke(this, true);
        }

        try
        {
            await EnsureCacheLoadedAsync(cancellationToken);

            var runtime = await _getRuntimeState.ExecuteAsync(
                forceRuntimeRefresh,
                cancellationToken);
            PublishRuntime(runtime);

            if (!ShouldRun())
            {
                return;
            }

            var profiles = await _listProfiles.ExecuteAsync(
                cancellationToken);
            var activeProfileId =
                runtime.Status ==
                ProfileRuntimeStatus.RunningKnownProfile
                    ? runtime.ActiveProfileId
                    : null;

            if (runtime.Status ==
                ProfileRuntimeStatus.RunningUnknownProfile ||
                activeProfileId is null)
            {
                _reader.StopActiveSession();
            }

            await RemoveDeletedProfilesAsync(
                profiles.Profiles,
                cancellationToken);

            HydrateFromCache(profiles.Profiles);
            PublishAll();

            var now = DateTimeOffset.Now;
            var orderedProfiles = profiles.Profiles
                .OrderByDescending(
                    profile =>
                        activeProfileId is not null &&
                        profile.Id == activeProfileId)
                .ThenBy(profile => profile.Name.Value)
                .ToArray();

            var probeBudget = kind == UsageRefreshKind.ManualForce
                ? int.MaxValue
                : ProfileUsageRefreshPolicy.ScheduledProbeBudget;
            var probesRemaining = probeBudget;

            foreach (var profile in orderedProfiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ShouldRun() || probesRemaining <= 0)
                {
                    break;
                }

                var isActive =
                    activeProfileId is not null &&
                    profile.Id == activeProfileId;
                _lastAttempts.TryGetValue(
                    profile.Id,
                    out var lastAttempt);
                _consecutiveFailures.TryGetValue(
                    profile.Id,
                    out var failureCount);

                if (!ignoreCache &&
                    IsFreshFromCache(
                        profile.Id,
                        isActive,
                        now))
                {
                    Publish(profile.Id);
                    continue;
                }

                if (!ignoreCache &&
                    !ProfileUsageRefreshPolicy.IsDue(
                        now,
                        lastAttempt == default
                            ? null
                            : lastAttempt,
                        isActive,
                        failureCount))
                {
                    Publish(profile.Id);
                    continue;
                }

                _lastAttempts[profile.Id] = now;
                var hasDisplayData =
                    _snapshots.TryGetValue(profile.Id, out var existing) &&
                    (existing.HasModels ||
                     existing.Status == ProfileQuotaStatus.Available);
                if (ignoreCache || !hasDisplayData)
                {
                    SetLoading(profile.Id);
                }

                ProfileQuotaSnapshot refreshed;
                try
                {
                    refreshed =
                        await _refreshProfile.ExecuteAsync(
                            profile.Id,
                            isActiveProfile: isActive,
                            cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    refreshed = new ProfileQuotaSnapshot(
                        profile.Id,
                        ProfileQuotaStatus.Failed);
                }

                await ApplyRefreshResultAsync(
                    refreshed,
                    isActive,
                    now,
                    cancellationToken);
                probesRemaining--;
            }

            PublishAll();
        }
        finally
        {
            if (showRefreshing)
            {
                RefreshingChanged?.Invoke(this, false);
            }

            _refreshGate.Release();
        }
    }

    private void HydrateFromCache(IReadOnlyList<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            if (!_snapshotCache.TryGet(profile.Id, out var entry) ||
                entry.Status == ProfileQuotaStatus.Loading)
            {
                continue;
            }

            _snapshots[profile.Id] = entry.ToSnapshot();
            _cacheCapturedAt[profile.Id] = entry.CapturedAt;
            if (!_lastAttempts.ContainsKey(profile.Id))
            {
                _lastAttempts[profile.Id] = entry.CapturedAt;
            }
        }
    }

    private bool IsFreshFromCache(
        ProfileId profileId,
        bool isActive,
        DateTimeOffset now)
    {
        if (_snapshotCache.TryGet(profileId, out var entry))
        {
            return ProfileUsageRefreshPolicy.IsCacheFresh(
                now,
                entry.CapturedAt,
                entry.Status,
                isActive);
        }

        if (_snapshots.TryGetValue(profileId, out var snapshot) &&
            _cacheCapturedAt.TryGetValue(profileId, out var capturedAt))
        {
            return ProfileUsageRefreshPolicy.IsCacheFresh(
                now,
                capturedAt,
                snapshot.Status,
                isActive);
        }

        return false;
    }

    private bool ShouldRun()
    {
        lock (_stateGate)
        {
            return !_disposed &&
                   _visibleSurfaceCount > 0 &&
                   _pauseCount == 0;
        }
    }

    private void PublishRuntime(ProfileRuntimeState runtime)
    {
        _publishedRuntimeState = runtime;
        RuntimeStateChanged?.Invoke(this, runtime);
    }

    private void SetLoading(ProfileId profileId)
    {
        _snapshots.TryGetValue(
            profileId,
            out var previous);
        var loading = new ProfileQuotaSnapshot(
            profileId,
            ProfileQuotaStatus.Loading,
            previous?.ModelList,
            previous?.AccountEmail,
            previous?.AccountPlan,
            previous?.LastSelectedModelId,
            previous?.MostRecentlyUsedModelId,
            previous?.LastSuccessfulAt);
        _snapshots[profileId] = loading;
        PublishIfChanged(loading);
    }

    private async Task ApplyRefreshResultAsync(
        ProfileQuotaSnapshot refreshed,
        bool isActive,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        _snapshots.TryGetValue(
            refreshed.ProfileId,
            out var previous);

        ProfileQuotaSnapshot stored;
        if (refreshed.Status == ProfileQuotaStatus.Available)
        {
            _consecutiveFailures[refreshed.ProfileId] = 0;
            stored = refreshed;
        }
        else
        {
            if (isActive)
            {
                _consecutiveFailures[refreshed.ProfileId] =
                    _consecutiveFailures.GetValueOrDefault(
                        refreshed.ProfileId) + 1;
            }

            stored = new ProfileQuotaSnapshot(
                refreshed.ProfileId,
                refreshed.Status,
                previous?.ModelList,
                previous?.AccountEmail,
                previous?.AccountPlan,
                previous?.LastSelectedModelId,
                previous?.MostRecentlyUsedModelId,
                previous?.LastSuccessfulAt);
        }

        _snapshots[refreshed.ProfileId] = stored;
        _cacheCapturedAt[refreshed.ProfileId] = capturedAt;

        if (stored.Status != ProfileQuotaStatus.Loading)
        {
            try
            {
                await _snapshotCache.SetAsync(
                    CachedProfileUsageEntry.FromSnapshot(
                        stored,
                        capturedAt),
                    cancellationToken);
            }
            catch (Exception)
                when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        PublishIfChanged(stored);
    }

    private async Task RemoveDeletedProfilesAsync(
        IReadOnlyList<Profile> profiles,
        CancellationToken cancellationToken)
    {
        var existing = profiles
            .Select(profile => profile.Id)
            .ToHashSet();
        foreach (var profileId in _snapshots.Keys
                     .Where(id => !existing.Contains(id))
                     .ToArray())
        {
            _snapshots.Remove(profileId);
            _publishedSnapshots.Remove(profileId);
            _lastAttempts.Remove(profileId);
            _cacheCapturedAt.Remove(profileId);
            _consecutiveFailures.Remove(profileId);
        }

        try
        {
            await _snapshotCache.RemoveMissingAsync(
                existing,
                cancellationToken);
        }
        catch (Exception)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Publish(ProfileId profileId)
    {
        if (_snapshots.TryGetValue(
                profileId,
                out var snapshot))
        {
            PublishIfChanged(snapshot);
        }
    }

    private void PublishAll()
    {
        foreach (var snapshot in _snapshots.Values)
        {
            PublishIfChanged(snapshot);
        }
    }

    private bool PublishIfChanged(ProfileQuotaSnapshot snapshot)
    {
        if (_publishedSnapshots.TryGetValue(
                snapshot.ProfileId,
                out var previous) &&
            HasSamePublishedSurface(previous, snapshot))
        {
            return false;
        }

        _publishedSnapshots[snapshot.ProfileId] = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
        return true;
    }

    internal static bool HasSamePublishedSurface(
        ProfileQuotaSnapshot previous,
        ProfileQuotaSnapshot current)
    {
        if (previous.ProfileId != current.ProfileId ||
            previous.Status != current.Status ||
            previous.AccountEmail != current.AccountEmail ||
            previous.AccountPlan != current.AccountPlan ||
            previous.LastSelectedModelId != current.LastSelectedModelId ||
            previous.MostRecentlyUsedModelId != current.MostRecentlyUsedModelId ||
            previous.ModelList.Count != current.ModelList.Count)
        {
            return false;
        }

        for (var i = 0; i < previous.ModelList.Count; i++)
        {
            if (previous.ModelList[i] != current.ModelList[i])
            {
                return false;
            }
        }

        return true;
    }

    private enum UsageRefreshKind
    {
        Scheduled,
        OpenSurface,
        ManualForce
    }

    private sealed class Lease : IDisposable
    {
        private Action? _release;

        public Lease(Action release)
        {
            _release = release;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
