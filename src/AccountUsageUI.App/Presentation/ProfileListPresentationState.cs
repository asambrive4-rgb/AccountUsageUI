using System.Collections.ObjectModel;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;

namespace AccountUsageUI.App.Presentation;

internal sealed class ProfileListPresentationState
{
    private readonly ObservableCollection<ProfileListItemViewModel>
        _inactiveProfiles = [];
    private readonly Dictionary<ProfileId, ProfileListItemViewModel>
        _profilesById = [];
    private IReadOnlyList<string> _favoriteFilters =
        FavoriteModelsPolicy.DefaultFavoriteModels;

    public ProfileListPresentationState()
    {
        InactiveProfiles =
            new ReadOnlyObservableCollection<ProfileListItemViewModel>(
                _inactiveProfiles);
    }

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; } =
        [];

    public ReadOnlyObservableCollection<ProfileListItemViewModel>
        InactiveProfiles { get; }

    public ProfileListItemViewModel? ActiveProfile { get; private set; }

    public ProfileListItemViewModel? DefaultPopupProfile =>
        ActiveProfile ?? Profiles.FirstOrDefault();

    public void Replace(IReadOnlyList<Profile> profiles)
    {
        var incomingIds = profiles
            .Select(profile => profile.Id)
            .ToHashSet();

        foreach (var profileId in _profilesById.Keys
                     .Where(profileId => !incomingIds.Contains(profileId))
                     .ToArray())
        {
            _profilesById.Remove(profileId);
        }

        var orderedProfiles =
            new ProfileListItemViewModel[profiles.Count];
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (!_profilesById.TryGetValue(
                    profile.Id,
                    out var item))
            {
                item = new ProfileListItemViewModel(
                    profile.Id,
                    profile.Name.Value);
                item.ApplyFavoriteFilters(_favoriteFilters);
                _profilesById[profile.Id] = item;
            }
            else
            {
                item.UpdateName(profile.Name.Value);
                item.ApplyFavoriteFilters(_favoriteFilters);
            }

            orderedProfiles[index] = item;
        }

        SynchronizeCollection(Profiles, orderedProfiles);
        RefreshDerivedCollections();
    }

    public void ApplyFavoriteFilters(IReadOnlyList<string> favoriteFilters)
    {
        _favoriteFilters = favoriteFilters;
        foreach (var profile in Profiles)
        {
            profile.ApplyFavoriteFilters(favoriteFilters);
        }
    }

    public void BeginOperation(
        ProfileId profileId,
        string status)
    {
        if (_profilesById.TryGetValue(profileId, out var target))
        {
            target.Status = status;
        }

        DisableAllActions();
    }

    public void ApplyUsageSnapshot(
        ProfileQuotaSnapshot snapshot)
    {
        if (_profilesById.TryGetValue(
                snapshot.ProfileId,
                out var profile))
        {
            profile.ApplyUsageSnapshot(snapshot);
        }
    }

    public string ApplyRuntimeState(
        ProfileRuntimeState state,
        bool canOperate)
    {
        var message = state.Status switch
        {
            ProfileRuntimeStatus.Stopped =>
                ApplyStopped(canOperate),
            ProfileRuntimeStatus.RunningKnownProfile =>
                ApplyKnownProfile(state.ActiveProfileId, canOperate),
            _ => ApplyUnknownProfile(canOperate)
        };

        RefreshDerivedCollections();
        return message;
    }

    public void DisableAllActions()
    {
        foreach (var profile in Profiles)
        {
            profile.IsRunEnabled = false;
            profile.IsDeleteEnabled = false;
        }
    }

    private static void SynchronizeCollection(
        ObservableCollection<ProfileListItemViewModel> collection,
        IReadOnlyList<ProfileListItemViewModel> desiredItems)
    {
        var desiredSet = desiredItems.ToHashSet();
        for (var index = collection.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(collection[index]))
            {
                collection.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredItems.Count; index++)
        {
            var desired = desiredItems[index];
            var currentIndex = collection.IndexOf(desired);
            if (currentIndex < 0)
            {
                collection.Insert(index, desired);
                continue;
            }

            if (currentIndex != index)
            {
                collection.Move(currentIndex, index);
            }
        }
    }

    private void RefreshDerivedCollections()
    {
        ActiveProfile = Profiles.FirstOrDefault(profile => profile.IsActive);
        SynchronizeCollection(
            _inactiveProfiles,
            Profiles
                .Where(profile => !profile.IsActive)
                .ToArray());
    }

    private string ApplyStopped(bool canOperate)
    {
        foreach (var profile in Profiles)
        {
            profile.IsActive = false;
            profile.Status = "준비됨";
            profile.ButtonText = "실행";
            profile.IsSwitchAction = false;
            profile.IsRunEnabled = canOperate;
            profile.IsDeleteEnabled = canOperate;
        }

        return "Antigravity 종료됨";
    }

    private string ApplyKnownProfile(
        ProfileId? activeProfileId,
        bool canOperate)
    {
        var active = Profiles.FirstOrDefault(
            profile => profile.Id == activeProfileId);
        foreach (var profile in Profiles)
        {
            profile.IsActive =
                active is not null &&
                profile.Id == active.Id;
            profile.Status = profile.IsActive
                ? "활성화됨"
                : "준비됨";
            profile.ButtonText = "전환";
            profile.IsSwitchAction =
                active is not null &&
                !profile.IsActive;
            profile.IsRunEnabled =
                !profile.IsActive &&
                active is not null &&
                canOperate;
            profile.IsDeleteEnabled = canOperate && !profile.IsActive;
        }

        if (active is not null)
        {
            var activeIndex = Profiles.IndexOf(active);
            if (activeIndex > 0)
            {
                Profiles.Move(activeIndex, 0);
            }
        }

        return active is null
            ? "Antigravity 실행 중 · 프로필 확인 불가"
            : $"Antigravity 실행 중 · {active.Name}";
    }

    private string ApplyUnknownProfile(bool canOperate)
    {
        foreach (var profile in Profiles)
        {
            profile.IsActive = false;
            profile.Status = "준비됨";
            profile.ButtonText = "전환";
            profile.IsSwitchAction = true;
            profile.IsRunEnabled = canOperate;
            profile.IsDeleteEnabled = canOperate;
        }

        return "Antigravity 실행 중 · 미등록 계정";
    }
}
