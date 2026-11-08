using System.Text;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Infrastructure.Profiles;
using AccountUsageUI.Infrastructure.Providers.Antigravity;

namespace AccountUsageUI.Tests;

[TestClass]
public sealed class ProfileStoreAndAuthSessionTests : IDisposable
{
    private readonly string _tempRoot;

    public ProfileStoreAndAuthSessionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "AccountUsageUI.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [TestMethod]
    public async Task WindowsProfileStore_SaveReadReplaceDelete_RoundTripsEncryptedCredentials()
    {
        var store = new WindowsProfileStore(Path.Combine(_tempRoot, "Profiles"));
        var profile = new Profile(
            ProfileId.New(),
            ProfileName.Create("개인 Pro"));

        var payload = Encoding.UTF8.GetBytes("{\"secret\":\"token-123\"}");
        await store.SaveAsync(profile, payload, CancellationToken.None);

        var listed = await store.ReadAllAsync(CancellationToken.None);
        Assert.HasCount(1, listed.Profiles);
        Assert.AreEqual("개인 Pro", listed.Profiles[0].Name.Value);

        var readPayload = await store.ReadCredentialAsync(profile.Id, CancellationToken.None);
        CollectionAssert.AreEqual(payload, readPayload);

        var newPayload = Encoding.UTF8.GetBytes("{\"secret\":\"token-456\"}");
        await store.ReplaceCredentialAsync(profile.Id, newPayload, CancellationToken.None);

        var updatedPayload = await store.ReadCredentialAsync(profile.Id, CancellationToken.None);
        CollectionAssert.AreEqual(newPayload, updatedPayload);

        await store.DeleteAsync(profile.Id, CancellationToken.None);
        Assert.IsEmpty((await store.ReadAllAsync(CancellationToken.None)).Profiles);
    }

    [TestMethod]
    public async Task AntigravityAuthenticationSession_CapturePrepareAndRestore_WorksWithoutTouchingSharedUserData()
    {
        var credentialStore = new InMemoryWindowsCredentialStore();
        var appDataDir = Path.Combine(_tempRoot, "AppData", "Antigravity");
        var recoveryDir = Path.Combine(_tempRoot, "LocalAppData", "Recovery");
        Directory.CreateDirectory(appDataDir);

        const string activeKeyringJson = """
        {
          "token": {
            "access_token": "ya29.test_access_token_1",
            "token_type": "Bearer",
            "refresh_token": "1//test_refresh_token_1"
          },
          "auth_method": "consumer",
          "id_token_email": "first@example.com"
        }
        """;

        credentialStore.WriteCredential(
            AntigravityAuthenticationSession.KeyringTargetName,
            AntigravityAuthenticationSession.KeyringUserName,
            Encoding.UTF8.GetBytes(activeKeyringJson));
        await File.WriteAllTextAsync(
            Path.Combine(appDataDir, "credential-user-scope"),
            "scope-user-123");
        await File.WriteAllTextAsync(
            Path.Combine(appDataDir, "app_storage.json"),
            """{"agy.onboarding.lastLoginUsername":"first@example.com","theme":"warm"}""");

        var session = new AntigravityAuthenticationSession(
            appDataDir,
            recoveryDir,
            credentialStore);

        // 1. Read current bundle without stopping app
        var bundleBytes = await session.ReadCurrentCredentialAsync(CancellationToken.None);
        Assert.IsNotNull(bundleBytes);

        var identityReader = new AntigravityCredentialIdentityReader();
        var accountId = identityReader.TryReadAccountId(bundleBytes);
        Assert.AreEqual("first@example.com", accountId);

        // 2. Prepare new login (clears keyring + backs up recovery.json)
        await session.PrepareForLoginAsync(CancellationToken.None);
        Assert.IsNull(credentialStore.ReadCredential(AntigravityAuthenticationSession.KeyringTargetName));
        Assert.IsTrue(await session.HasPendingRecoveryAsync(CancellationToken.None));

        // 3. Cancel new login -> restores original credentials
        await session.RestorePreviousAsync(CancellationToken.None);
        await session.ClearRecoveryAsync(CancellationToken.None);
        Assert.IsNotNull(credentialStore.ReadCredential(AntigravityAuthenticationSession.KeyringTargetName));
        Assert.IsFalse(await session.HasPendingRecoveryAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateProfileLoginUseCase_CaptureCurrentAsync_SavesActiveAccountImmediately()
    {
        var store = new WindowsProfileStore(Path.Combine(_tempRoot, "Store2"));
        var credentialStore = new InMemoryWindowsCredentialStore();
        var appDataDir = Path.Combine(_tempRoot, "AppData2", "Antigravity");
        var recoveryDir = Path.Combine(_tempRoot, "Local2", "Recovery");
        Directory.CreateDirectory(appDataDir);

        credentialStore.WriteCredential(
            AntigravityAuthenticationSession.KeyringTargetName,
            AntigravityAuthenticationSession.KeyringUserName,
            Encoding.UTF8.GetBytes(
                """
                {
                  "token": {
                    "access_token": "ya29.live_token",
                    "refresh_token": "1//live_refresh"
                  },
                  "id_token_email": "live@example.com"
                }
                """));

        var authSession = new AntigravityAuthenticationSession(
            appDataDir,
            recoveryDir,
            credentialStore);

        var fakeProcessController = new FakeAppProcessController { IsRunning = true };
        var coordinator = new ProfileOperationCoordinator();

        var useCase = new CreateProfileLoginUseCase(
            store,
            authSession,
            fakeProcessController,
            coordinator);

        var result = await useCase.CaptureCurrentAsync("메인 계정", CancellationToken.None);

        Assert.AreEqual(CreateProfileLoginStatus.Created, result.Status);
        Assert.IsNotNull(result.Profile);
        Assert.AreEqual("메인 계정", result.Profile.Name.Value);
        // Ensure Antigravity was NOT stopped during instant capture!
        Assert.AreEqual(0, fakeProcessController.StopCallCount);
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

    private sealed class InMemoryWindowsCredentialStore : IWindowsCredentialStore
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

        public byte[]? ReadCredential(string targetName) =>
            _items.TryGetValue(targetName, out var value) ? value.ToArray() : null;

        public void WriteCredential(
            string targetName,
            string userName,
            ReadOnlySpan<byte> secretBytes) =>
            _items[targetName] = secretBytes.ToArray();

        public void DeleteCredential(string targetName) =>
            _items.Remove(targetName);
    }

    private sealed class FakeAppProcessController : IAppProcessController
    {
        public bool IsRunning { get; set; }

        public int StopCallCount { get; private set; }

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(IsRunning);

        public Task<AppStopStatus> RequestStopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            IsRunning = false;
            return Task.FromResult(AppStopStatus.Stopped);
        }

        public Task<bool> ForceStopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
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
