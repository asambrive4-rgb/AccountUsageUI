using System.Security.Cryptography;

namespace AccountUsageUI.Core.Profiles;

public sealed class GetProfileRuntimeStateUseCase
{
    private static readonly TimeSpan RunningStateCacheDuration =
        TimeSpan.FromSeconds(5);

    private readonly IProfileStore _profileStore;
    private readonly IAuthenticationSession _authenticationSession;
    private readonly IAppProcessController _processController;
    private readonly ICredentialIdentityReader _identityReader;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<ProfileId, CachedProfileCredential>
        _credentialCache = [];
    private ProfileId? _lastKnownActiveProfileId;
    private HashSet<ProfileId>? _knownProfileIds;
    private CachedRuntimeState? _cachedState;

    public GetProfileRuntimeStateUseCase(
        IProfileStore profileStore,
        IAuthenticationSession authenticationSession,
        IAppProcessController processController)
        : this(
            profileStore,
            authenticationSession,
            processController,
            new NoCredentialIdentityReader())
    {
    }

    public GetProfileRuntimeStateUseCase(
        IProfileStore profileStore,
        IAuthenticationSession authenticationSession,
        IAppProcessController processController,
        ICredentialIdentityReader identityReader)
        : this(
            profileStore,
            authenticationSession,
            processController,
            identityReader,
            TimeProvider.System)
    {
    }

    public GetProfileRuntimeStateUseCase(
        IProfileStore profileStore,
        IAuthenticationSession authenticationSession,
        IAppProcessController processController,
        ICredentialIdentityReader identityReader,
        TimeProvider timeProvider)
    {
        _profileStore = profileStore;
        _authenticationSession = authenticationSession;
        _processController = processController;
        _identityReader = identityReader;
        _timeProvider = timeProvider;
    }

    public Task<ProfileRuntimeState> ExecuteAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            forceRefresh: false,
            cancellationToken);

    public async Task<ProfileRuntimeState> ExecuteAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (forceRefresh)
        {
            InvalidateCredentialCache();
        }

        if (!await _processController.IsRunningAsync(cancellationToken))
        {
            var stopped = new ProfileRuntimeState(
                ProfileRuntimeStatus.Stopped);
            Cache(stopped);
            return stopped;
        }

        var cached = GetFreshCachedRunningState(forceRefresh);
        if (cached is not null)
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            cached = GetFreshCachedRunningState(forceRefresh);
            if (cached is not null)
            {
                return cached;
            }

            var refreshed = await ResolveRunningStateAsync(
                cancellationToken);
            Cache(refreshed);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<ProfileRuntimeState> ResolveRunningStateAsync(
        CancellationToken cancellationToken)
    {
        byte[]? currentCredential = null;
        try
        {
            currentCredential =
                await _authenticationSession.ReadCurrentCredentialAsync(
                    cancellationToken);
            if (currentCredential is null)
            {
                _lastKnownActiveProfileId = null;
                return Unknown();
            }

            var currentHash = SHA256.HashData(currentCredential);
            var currentAccountId =
                _identityReader.TryReadAccountId(currentCredential);

            var stored = await _profileStore.ReadAllAsync(
                cancellationToken);

            SynchronizeProfileList(stored.Profiles);

            if (_lastKnownActiveProfileId is not null &&
                stored.Profiles.Any(p => p.Id == _lastKnownActiveProfileId.Value))
            {
                var activeId = _lastKnownActiveProfileId.Value;
                var activeCredential = await GetOrLoadProfileCredentialAsync(
                    activeId,
                    forceReload: true,
                    cancellationToken);

                if (activeCredential is not null &&
                    IsCredentialMatch(
                        activeCredential,
                        currentHash,
                        currentAccountId))
                {
                    var hasDuplicateMatch = false;
                    foreach (var profile in stored.Profiles)
                    {
                        if (profile.Id == activeId)
                        {
                            continue;
                        }

                        var otherCredential =
                            await GetOrLoadProfileCredentialAsync(
                                profile.Id,
                                forceReload: false,
                                cancellationToken);

                        if (otherCredential is not null &&
                            IsCredentialMatch(
                                otherCredential,
                                currentHash,
                                currentAccountId))
                        {
                            hasDuplicateMatch = true;
                            break;
                        }
                    }

                    if (!hasDuplicateMatch)
                    {
                        return new ProfileRuntimeState(
                            ProfileRuntimeStatus.RunningKnownProfile,
                            activeId);
                    }

                    _lastKnownActiveProfileId = null;
                    return Unknown();
                }

                _lastKnownActiveProfileId = null;
            }

            var matches = new List<ProfileId>();
            foreach (var profile in stored.Profiles)
            {
                var profileCredential =
                    await GetOrLoadProfileCredentialAsync(
                        profile.Id,
                        forceReload: false,
                        cancellationToken);

                if (profileCredential is not null &&
                    IsCredentialMatch(
                        profileCredential,
                        currentHash,
                        currentAccountId))
                {
                    matches.Add(profile.Id);
                }
            }

            if (matches.Count == 1)
            {
                _lastKnownActiveProfileId = matches[0];
                return new ProfileRuntimeState(
                    ProfileRuntimeStatus.RunningKnownProfile,
                    matches[0]);
            }

            _lastKnownActiveProfileId = null;
            return Unknown();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _lastKnownActiveProfileId = null;
            return Unknown();
        }
        finally
        {
            if (currentCredential is not null)
            {
                CryptographicOperations.ZeroMemory(
                    currentCredential);
            }
        }
    }

    private void InvalidateCredentialCache()
    {
        lock (_cacheGate)
        {
            _cachedState = null;
            _credentialCache.Clear();
            _lastKnownActiveProfileId = null;
            _knownProfileIds = null;
        }
    }

    private void SynchronizeProfileList(
        IReadOnlyList<Profile> profiles)
    {
        var currentProfileIds = profiles
            .Select(p => p.Id)
            .ToHashSet();

        lock (_cacheGate)
        {
            if (_knownProfileIds is null ||
                !_knownProfileIds.SetEquals(currentProfileIds))
            {
                _credentialCache.Clear();
                _lastKnownActiveProfileId = null;
                _knownProfileIds = currentProfileIds;
            }
        }
    }

    private async Task<CachedProfileCredential?> GetOrLoadProfileCredentialAsync(
        ProfileId profileId,
        bool forceReload,
        CancellationToken cancellationToken)
    {
        if (!forceReload)
        {
            lock (_cacheGate)
            {
                if (_credentialCache.TryGetValue(
                    profileId,
                    out var cached))
                {
                    return cached;
                }
            }
        }

        byte[]? storedCredential = null;
        try
        {
            storedCredential =
                await _profileStore.ReadCredentialAsync(
                    profileId,
                    cancellationToken);

            var hash = SHA256.HashData(storedCredential);
            var accountId =
                _identityReader.TryReadAccountId(storedCredential);

            var entry = new CachedProfileCredential(
                hash,
                accountId);

            lock (_cacheGate)
            {
                _credentialCache[profileId] = entry;
            }

            return entry;
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidDataException or
                  CryptographicException)
        {
            lock (_cacheGate)
            {
                _credentialCache.Remove(profileId);
            }

            return null;
        }
        finally
        {
            if (storedCredential is not null)
            {
                CryptographicOperations.ZeroMemory(
                    storedCredential);
            }
        }
    }

    private static bool IsCredentialMatch(
        CachedProfileCredential profileCredential,
        byte[] currentHash,
        string? currentAccountId)
    {
        if (CryptographicOperations.FixedTimeEquals(
            profileCredential.Hash,
            currentHash))
        {
            return true;
        }

        if (currentAccountId is not null &&
            profileCredential.AccountId is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(
                currentAccountId,
                profileCredential.AccountId))
        {
            return true;
        }

        return false;
    }

    private ProfileRuntimeState? GetFreshCachedRunningState(
        bool forceRefresh)
    {
        if (forceRefresh)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_cacheGate)
        {
            if (_cachedState is null ||
                _cachedState.State.Status ==
                ProfileRuntimeStatus.Stopped ||
                now - _cachedState.CapturedAt >
                RunningStateCacheDuration)
            {
                return null;
            }

            return _cachedState.State;
        }
    }

    private void Cache(ProfileRuntimeState state)
    {
        lock (_cacheGate)
        {
            _cachedState = new CachedRuntimeState(
                state,
                _timeProvider.GetUtcNow());
        }
    }

    private static ProfileRuntimeState Unknown() =>
        new(ProfileRuntimeStatus.RunningUnknownProfile);

    private sealed record CachedRuntimeState(
        ProfileRuntimeState State,
        DateTimeOffset CapturedAt);

    private sealed record CachedProfileCredential(
        byte[] Hash,
        string? AccountId);

    private sealed class NoCredentialIdentityReader
        : ICredentialIdentityReader
    {
        public string? TryReadAccountId(
            ReadOnlySpan<byte> credential) => null;
    }
}
