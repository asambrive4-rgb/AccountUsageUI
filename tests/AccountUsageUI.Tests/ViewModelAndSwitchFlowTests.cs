using System.Text;
using AccountUsageUI.App.Presentation;
using AccountUsageUI.Core.Installation;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Core.Usage;
using AccountUsageUI.Infrastructure.Profiles;
using AccountUsageUI.Infrastructure.Providers.Antigravity;
using AccountUsageUI.Infrastructure.Usage;

namespace AccountUsageUI.Tests;

[TestClass]
public sealed class ViewModelAndSwitchFlowTests : IDisposable
{
    private readonly string _tempRoot;

    public ViewModelAndSwitchFlowTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AccountUsageUI.VmTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestMethod]
    public async Task SwitchProfileUseCase_SwitchesBetweenTwoProfilesAndUpdatesActiveRuntimeState()
    {
        var store = new WindowsProfileStore(Path.Combine(_tempRoot, "Profiles"));
        var credentialStore = new InMemoryWindowsCredentialStore();
        var appDataDir = Path.Combine(_tempRoot, "AppData", "Antigravity");
        var recoveryDir = Path.Combine(_tempRoot, "Local", "Recovery");
        Directory.CreateDirectory(appDataDir);

        var authSession = new AntigravityAuthenticationSession(
            appDataDir,
            recoveryDir,
            credentialStore);
        var identityReader = new AntigravityCredentialIdentityReader();
        var processController = new FakeAppProcessController { IsRunning = true };
        var coordinator = new ProfileOperationCoordinator();

        var profileA = new Profile(ProfileId.New(), ProfileName.Create("계정 A"));
        var profileB = new Profile(ProfileId.New(), ProfileName.Create("계정 B"));

        var credA = Encoding.UTF8.GetBytes(
            """{"token":{"access_token":"ya29.a","refresh_token":"1//a"},"id_token_email":"a@example.com"}""");
        var credB = Encoding.UTF8.GetBytes(
            """{"token":{"access_token":"ya29.b","refresh_token":"1//b"},"id_token_email":"b@example.com"}""");

        await store.SaveAsync(profileA, credA, CancellationToken.None);
        await store.SaveAsync(profileB, credB, CancellationToken.None);

        // Start with Profile A active in Windows Credential Manager
        credentialStore.WriteCredential(
            AntigravityAuthenticationSession.KeyringTargetName,
            AntigravityAuthenticationSession.KeyringUserName,
            credA);

        var runtimeUseCase = new GetProfileRuntimeStateUseCase(
            store,
            authSession,
            processController,
            identityReader);

        var initialRuntime = await runtimeUseCase.ExecuteAsync(forceRefresh: true, CancellationToken.None);
        Assert.AreEqual(ProfileRuntimeStatus.RunningKnownProfile, initialRuntime.Status);
        Assert.AreEqual(profileA.Id, initialRuntime.ActiveProfileId);

        // Switch to Profile B
        var switchUseCase = new SwitchProfileUseCase(
            store,
            authSession,
            processController,
            coordinator,
            identityReader);

        var switchResult = await switchUseCase.ExecuteAsync(profileB.Id, CancellationToken.None);
        Assert.AreEqual(SwitchProfileStatus.Switched, switchResult.Status);

        var updatedRuntime = await runtimeUseCase.ExecuteAsync(forceRefresh: true, CancellationToken.None);
        Assert.AreEqual(ProfileRuntimeStatus.RunningKnownProfile, updatedRuntime.Status);
        Assert.AreEqual(profileB.Id, updatedRuntime.ActiveProfileId);
    }

    [TestMethod]
    public void SettingsViewModel_AddCustomModelAndResetToDefaults_UpdatesSelection()
    {
        var vm = new SettingsViewModel(
            FavoriteModelsPolicy.CatalogModels,
            ["Gemini Models", "Gemini 4 Argon"]);

        Assert.HasCount(2, vm.GetSelectedModels());

        vm.CustomModelInput = "Gemini 4 Ultra Preview";
        vm.AddCustomModel();

        var afterAdd = vm.GetSelectedModels();
        Assert.HasCount(3, afterAdd);
        Assert.AreEqual("Gemini 4 Ultra Preview", afterAdd[0]);

        vm.ResetToDefaults();
        var afterReset = vm.GetSelectedModels();
        CollectionAssert.AreEquivalent(
            FavoriteModelsPolicy.DefaultFavoriteModels.ToArray(),
            afterReset.ToArray());
    }

    [TestMethod]
    public async Task MainWindowViewModel_AppliesUsageSummaryGroupsAndFormatsLikeScreenshot()
    {
        var store = new WindowsProfileStore(Path.Combine(_tempRoot, "VmProfiles"));
        var credentialStore = new InMemoryWindowsCredentialStore();
        var appDataDir = Path.Combine(_tempRoot, "VmAppData", "Antigravity");
        var recoveryDir = Path.Combine(_tempRoot, "VmLocal", "Recovery");
        var settingsDir = Path.Combine(_tempRoot, "VmSettings");
        Directory.CreateDirectory(appDataDir);

        var credA = Encoding.UTF8.GetBytes(
            """{"token":{"access_token":"ya29.a","refresh_token":"1//a"},"id_token_email":"a@example.com"}""");
        var profileA = new Profile(ProfileId.New(), ProfileName.Create("Pro 계정"));
        await store.SaveAsync(profileA, credA, CancellationToken.None);

        credentialStore.WriteCredential(
            AntigravityAuthenticationSession.KeyringTargetName,
            AntigravityAuthenticationSession.KeyringUserName,
            credA);

        var authSession = new AntigravityAuthenticationSession(appDataDir, recoveryDir, credentialStore);
        var processController = new FakeAppProcessController { IsRunning = true };
        var identityReader = new AntigravityCredentialIdentityReader();
        var coordinator = new ProfileOperationCoordinator();
        var locator = new FakeInstallationLocator();

        var vm = new MainWindowViewModel(
            new DetectAppInstallationUseCase(locator),
            new ListProfilesUseCase(store),
            new CreateProfileLoginUseCase(store, authSession, processController, coordinator),
            new RunProfileUseCase(store, authSession, processController, coordinator, identityReader),
            new SwitchProfileUseCase(store, authSession, processController, coordinator, identityReader),
            new DeleteProfileUseCase(store, authSession, processController, coordinator, identityReader),
            new GetProfileRuntimeStateUseCase(store, authSession, processController, identityReader),
            new JsonUserSettingsStore(Path.Combine(settingsDir, "user-settings.json")));

        await vm.InitializeAsync(CancellationToken.None);

        Assert.IsTrue(vm.HasActiveProfile);
        Assert.IsNotNull(vm.ActiveProfile);
        Assert.AreEqual("Pro 계정", vm.ActiveProfile.Name);

        var now = DateTimeOffset.UtcNow;
        var snapshot = new ProfileQuotaSnapshot(
            profileA.Id,
            ProfileQuotaStatus.Available,
            [
                new ModelQuotaWindow("gemini-weekly", "Weekly Limit Remaining", 1.0, now.AddDays(6), "Gemini Models", "weekly", null),
                new ModelQuotaWindow("gemini-5h", "Five Hour Limit Remaining", 1.0, now.AddHours(5), "Gemini Models", "5h", null),
                new ModelQuotaWindow("3p-weekly", "Weekly Limit Remaining", 1.0, now.AddDays(6), "Claude and GPT models", "weekly", null),
                new ModelQuotaWindow("3p-5h", "Five Hour Limit Remaining", 1.0, now.AddHours(5), "Claude and GPT models", "5h", null),
                new ModelQuotaWindow(
                    "gemini-argon-wtu-weekly",
                    "Gemini 4 Argon Weekly Limit",
                    0.88,
                    now.AddDays(6).AddHours(23).AddMinutes(30),
                    "Gemini 4 Argon",
                    "weekly",
                    "You have used some of your weekly limit")
            ],
            "a@example.com",
            "Google AI Pro",
            null,
            null,
            now);

        vm.ApplyUsageSnapshot(snapshot);

        Assert.IsTrue(vm.ActiveProfile.HasUsageData);
        Assert.HasCount(3, vm.ActiveProfile.QuotaGroups);
        Assert.HasCount(5, vm.ActiveProfile.FavoriteModelQuotas);

        // Group 1: Gemini Models (Header visible, 2 nested buckets: Weekly limit, 5h limit)
        var group1 = vm.ActiveProfile.QuotaGroups[0];
        Assert.AreEqual("Gemini Models", group1.GroupTitle);
        Assert.IsTrue(group1.HasGroupHeader);
        Assert.HasCount(2, group1.Buckets);
        Assert.AreEqual("Weekly limit", group1.Buckets[0].DisplayName);
        Assert.AreEqual("100% left", group1.Buckets[0].PercentLeftText);
        Assert.IsFalse(group1.Buckets[0].HasSecondRow);
        Assert.AreEqual("5h limit", group1.Buckets[1].DisplayName);
        Assert.AreEqual("100% left", group1.Buckets[1].PercentLeftText);

        // Group 2: Claude and GPT models (Header visible, 2 nested buckets: Weekly limit, 5h limit)
        var group2 = vm.ActiveProfile.QuotaGroups[1];
        Assert.AreEqual("Claude and GPT models", group2.GroupTitle);
        Assert.IsTrue(group2.HasGroupHeader);
        Assert.HasCount(2, group2.Buckets);

        // Group 3: Gemini 4 Argon (Single bucket -> Header collapsed, Title = "Gemini 4 Argon", Detail = "Weekly limit", Reset = "Resets in 6d 23h")
        var group3 = vm.ActiveProfile.QuotaGroups[2];
        Assert.IsFalse(group3.HasGroupHeader);
        Assert.HasCount(1, group3.Buckets);
        Assert.AreEqual("Gemini 4 Argon", group3.Buckets[0].DisplayName);
        Assert.AreEqual("88% left", group3.Buckets[0].PercentLeftText);
        Assert.AreEqual("Weekly limit", group3.Buckets[0].DetailLabel);
        Assert.AreEqual("Resets in 6d 23h", group3.Buckets[0].ResetTimeText);
        Assert.IsTrue(group3.Buckets[0].HasSecondRow);

        // Mini popup RecentModelUsage
        Assert.AreEqual("Gemini 4 Argon", vm.ActiveProfile.RecentModelUsage.ShortName);
        Assert.AreEqual("88% left", vm.ActiveProfile.RecentModelUsage.PercentLeftText);
        Assert.AreEqual("Weekly limit", vm.ActiveProfile.RecentModelUsage.DetailLabel);
        Assert.AreEqual("Resets in 6d 23h", vm.ActiveProfile.RecentModelUsage.ResetTimeText);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeInstallationLocator : IAppInstallationLocator
    {
        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class InMemoryWindowsCredentialStore : IWindowsCredentialStore
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

        public byte[]? ReadCredential(string targetName) =>
            _items.TryGetValue(targetName, out var value) ? value.ToArray() : null;

        public void WriteCredential(string targetName, string userName, ReadOnlySpan<byte> secretBytes) =>
            _items[targetName] = secretBytes.ToArray();

        public void DeleteCredential(string targetName) =>
            _items.Remove(targetName);
    }

    private sealed class FakeAppProcessController : IAppProcessController
    {
        public bool IsRunning { get; set; }

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(IsRunning);

        public Task<AppStopStatus> RequestStopAsync(CancellationToken cancellationToken)
        {
            IsRunning = false;
            return Task.FromResult(AppStopStatus.Stopped);
        }

        public Task<bool> ForceStopAsync(CancellationToken cancellationToken)
        {
            IsRunning = false;
            return Task.FromResult(true);
        }

        public Task<AppLaunchStatus> LaunchAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            return Task.FromResult(AppLaunchStatus.Launched);
        }

        public Task<bool> WaitForRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(IsRunning);
    }
}
