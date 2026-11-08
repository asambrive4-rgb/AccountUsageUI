using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AccountUsageUI.Core.Profiles;
using AccountUsageUI.Infrastructure.Profiles;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public sealed class AntigravityAuthenticationSession : IAuthenticationSession
{
    public const string KeyringTargetName = "gemini:antigravity";
    public const string KeyringUserName = "antigravity";

    private const string StateFileName = "recovery.json";
    private const string CredentialFileName = "previous-credential.bin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _antigravityAppDataRoot;
    private readonly string _recoveryRoot;
    private readonly IWindowsCredentialStore _credentialStore;
    private readonly CurrentUserDataProtector _protector = new();
    private readonly CurrentUserStorageAcl _storageAcl = new();

    public AntigravityAuthenticationSession()
        : this(
            GetDefaultAntigravityAppDataRoot(),
            GetDefaultRecoveryRoot(),
            new NativeWindowsCredentialStore())
    {
    }

    public AntigravityAuthenticationSession(
        string antigravityAppDataRoot,
        string recoveryRoot,
        IWindowsCredentialStore credentialStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(antigravityAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryRoot);
        ArgumentNullException.ThrowIfNull(credentialStore);

        _antigravityAppDataRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(antigravityAppDataRoot));
        _recoveryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(recoveryRoot));
        _credentialStore = credentialStore;
    }

    public static string GetDefaultAntigravityAppDataRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "Antigravity");
    }

    public static string GetDefaultRecoveryRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "AccountUsageUI",
            "LoginRecovery",
            "v1");
    }

    public Task<bool> HasPendingRecoveryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            File.Exists(StatePath) ||
            File.Exists(RecoveryCredentialPath));
    }

    public async Task PrepareForLoginAsync(
        CancellationToken cancellationToken)
    {
        await PrepareChangeAsync(
            replacementCredential: null,
            cancellationToken);
    }

    public async Task PrepareForProfileAsync(
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken)
    {
        await PrepareChangeAsync(
            credential,
            cancellationToken);
    }

    public async Task<byte[]?> ReadCurrentCredentialAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAppDataRootIsSafe();

        var keyringBytes = _credentialStore.ReadCredential(KeyringTargetName);
        if (keyringBytes is null || keyringBytes.Length == 0)
        {
            return null;
        }

        try
        {
            var keyringJson = Encoding.UTF8.GetString(keyringBytes);
            if (!IsValidKeyringPayload(keyringJson))
            {
                return null;
            }

            string? userScope = null;
            if (File.Exists(CredentialUserScopePath))
            {
                EnsureFileIsNotReparsePoint(CredentialUserScopePath);
                userScope = await File.ReadAllTextAsync(
                    CredentialUserScopePath,
                    Encoding.UTF8,
                    cancellationToken);
            }

            var email = ExtractEmail(keyringJson);
            var bundle = new AntigravityCredentialBundle(
                SchemaVersion: 1,
                KeyringJson: keyringJson,
                CredentialUserScope: userScope,
                LastLoginEmail: email);

            return JsonSerializer.SerializeToUtf8Bytes(
                bundle,
                JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyringBytes);
        }
    }

    public async Task RestorePreviousAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAppDataRootIsSafe();

        var state = await ReadRecoveryStateAsync(cancellationToken);
        if (!state.HadCredential)
        {
            ClearCurrentAuthState();
            return;
        }

        if (!File.Exists(RecoveryCredentialPath))
        {
            throw new InvalidDataException(
                "복구할 인증 사본을 찾을 수 없습니다.");
        }

        EnsureFileIsNotReparsePoint(RecoveryCredentialPath);
        var protectedCredential = await File.ReadAllBytesAsync(
            RecoveryCredentialPath,
            cancellationToken);

        try
        {
            var credential = _protector.Unprotect(protectedCredential);

            try
            {
                await ApplyCredentialBundleAsync(
                    credential,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credential);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedCredential);
        }
    }

    public Task ClearRecoveryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureRecoveryRoot();

        DeleteExpectedFile(RecoveryCredentialPath);
        DeleteExpectedFile(StatePath);
        return Task.CompletedTask;
    }

    private async Task PrepareChangeAsync(
        ReadOnlyMemory<byte>? replacementCredential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAppDataRootIsSafe();
        EnsureRecoveryRoot();

        if (File.Exists(StatePath) ||
            File.Exists(RecoveryCredentialPath))
        {
            throw new InvalidOperationException(
                "이전 로그인 작업 복구가 먼저 필요합니다.");
        }

        byte[]? currentCredential = null;
        byte[]? protectedCredential = null;

        try
        {
            currentCredential = await ReadCurrentCredentialAsync(
                cancellationToken);
            var hadCredential = currentCredential is not null;

            if (hadCredential)
            {
                protectedCredential = _protector.Protect(currentCredential!);
                await AtomicFileWriter.WriteAsync(
                    RecoveryCredentialPath,
                    protectedCredential,
                    cancellationToken);
            }

            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(
                new RecoveryState(hadCredential),
                JsonOptions);
            try
            {
                await AtomicFileWriter.WriteAsync(
                    StatePath,
                    stateBytes,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stateBytes);
            }

            if (replacementCredential is not null)
            {
                await ApplyCredentialBundleAsync(
                    replacementCredential.Value,
                    cancellationToken);
            }
            else
            {
                ClearCurrentAuthState();
            }
        }
        catch
        {
            if (File.Exists(StatePath))
            {
                try
                {
                    await RestorePreviousAsync(CancellationToken.None);
                    await ClearRecoveryAsync(CancellationToken.None);
                }
                catch
                {
                    // 복구 정보는 다음 앱 실행에서 다시 사용할 수 있게 남긴다.
                }
            }
            else if (File.Exists(RecoveryCredentialPath))
            {
                File.Delete(RecoveryCredentialPath);
            }

            throw;
        }
        finally
        {
            if (currentCredential is not null)
            {
                CryptographicOperations.ZeroMemory(currentCredential);
            }

            if (protectedCredential is not null)
            {
                CryptographicOperations.ZeroMemory(protectedCredential);
            }
        }
    }

    private async Task ApplyCredentialBundleAsync(
        ReadOnlyMemory<byte> credentialBytes,
        CancellationToken cancellationToken)
    {
        var parsed = ParseBundleOrRawKeyring(credentialBytes.Span);
        var keyringBytes = Encoding.UTF8.GetBytes(parsed.KeyringJson);
        try
        {
            _credentialStore.WriteCredential(
                KeyringTargetName,
                KeyringUserName,
                keyringBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyringBytes);
        }

        if (!string.IsNullOrWhiteSpace(parsed.CredentialUserScope))
        {
            var scopeBytes = Encoding.UTF8.GetBytes(parsed.CredentialUserScope);
            await AtomicFileWriter.WriteAsync(
                CredentialUserScopePath,
                scopeBytes,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(parsed.LastLoginEmail))
        {
            await UpdateAppStorageLastLoginEmailAsync(
                parsed.LastLoginEmail,
                cancellationToken);
        }
    }

    private void ClearCurrentAuthState()
    {
        _credentialStore.DeleteCredential(KeyringTargetName);
        if (File.Exists(CredentialUserScopePath))
        {
            EnsureFileIsNotReparsePoint(CredentialUserScopePath);
            File.Delete(CredentialUserScopePath);
        }
    }

    private async Task UpdateAppStorageLastLoginEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(AppStoragePath))
            {
                return;
            }

            EnsureFileIsNotReparsePoint(AppStoragePath);
            var rawJson = await File.ReadAllTextAsync(
                AppStoragePath,
                Encoding.UTF8,
                cancellationToken);
            var rootNode = JsonNode.Parse(rawJson) as JsonObject;
            if (rootNode is null)
            {
                return;
            }

            rootNode["agy.onboarding.lastLoginUsername"] = email;
            var updatedBytes = Encoding.UTF8.GetBytes(
                rootNode.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
            await AtomicFileWriter.WriteAsync(
                AppStoragePath,
                updatedBytes,
                cancellationToken);
        }
        catch
        {
            // app_storage.json 보조 필드 갱신 실패가 핵심 인증 전환을 막으면 안 된다.
        }
    }

    internal static AntigravityCredentialBundle ParseBundleOrRawKeyring(
        ReadOnlySpan<byte> credentialBytes)
    {
        var rawText = Encoding.UTF8.GetString(credentialBytes);
        using var document = JsonDocument.Parse(rawText);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("keyringJson", out var keyringProp) &&
            keyringProp.ValueKind == JsonValueKind.String)
        {
            var keyringJson = keyringProp.GetString()
                ?? throw new InvalidDataException("인증 토큰 정보가 비어 있습니다.");
            var userScope = root.TryGetProperty("credentialUserScope", out var scopeProp) &&
                            scopeProp.ValueKind == JsonValueKind.String
                ? scopeProp.GetString()
                : null;
            var email = root.TryGetProperty("lastLoginEmail", out var emailProp) &&
                        emailProp.ValueKind == JsonValueKind.String
                ? emailProp.GetString()
                : ExtractEmail(keyringJson);

            return new AntigravityCredentialBundle(
                SchemaVersion: 1,
                KeyringJson: keyringJson,
                CredentialUserScope: userScope,
                LastLoginEmail: email);
        }

        if (!IsValidKeyringPayload(rawText))
        {
            throw new InvalidDataException("유효한 Antigravity 인증 형식이 아닙니다.");
        }

        return new AntigravityCredentialBundle(
            SchemaVersion: 1,
            KeyringJson: rawText,
            CredentialUserScope: null,
            LastLoginEmail: ExtractEmail(rawText));
    }

    internal static byte[] ReplaceKeyringJsonInBundle(
        ReadOnlySpan<byte> originalBundleBytes,
        string updatedKeyringJson)
    {
        var bundle = ParseBundleOrRawKeyring(originalBundleBytes);
        var updated = bundle with
        {
            KeyringJson = updatedKeyringJson,
            LastLoginEmail = ExtractEmail(updatedKeyringJson) ?? bundle.LastLoginEmail
        };
        return JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
    }

    private static bool IsValidKeyringPayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasAccess = token.TryGetProperty("access_token", out var access) &&
                            access.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(access.GetString());
            var hasRefresh = token.TryGetProperty("refresh_token", out var refresh) &&
                             refresh.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(refresh.GetString());

            return hasAccess || hasRefresh;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractEmail(string keyringJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(keyringJson);
            if (doc.RootElement.TryGetProperty("id_token_email", out var emailProp) &&
                emailProp.ValueKind == JsonValueKind.String)
            {
                var email = emailProp.GetString();
                if (!string.IsNullOrWhiteSpace(email))
                {
                    return email.Trim();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private string CredentialUserScopePath =>
        Path.Combine(_antigravityAppDataRoot, "credential-user-scope");

    private string AppStoragePath =>
        Path.Combine(_antigravityAppDataRoot, "app_storage.json");

    private string StatePath =>
        Path.Combine(_recoveryRoot, StateFileName);

    private string RecoveryCredentialPath =>
        Path.Combine(_recoveryRoot, CredentialFileName);

    private async Task<RecoveryState> ReadRecoveryStateAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            throw new InvalidDataException(
                "로그인 작업 복구 정보를 찾을 수 없습니다.");
        }

        EnsureFileIsNotReparsePoint(StatePath);
        await using var stream = new FileStream(
            StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<RecoveryState>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException(
                   "로그인 작업 복구 정보를 읽을 수 없습니다.");
    }

    private void EnsureAppDataRootIsSafe()
    {
        Directory.CreateDirectory(_antigravityAppDataRoot);
        if ((File.GetAttributes(_antigravityAppDataRoot) &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "재분석 지점은 Antigravity 데이터 폴더로 사용할 수 없습니다.");
        }
    }

    private void EnsureRecoveryRoot()
    {
        if (Directory.Exists(_recoveryRoot) &&
            (File.GetAttributes(_recoveryRoot) &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "재분석 지점은 복구 저장소로 사용할 수 없습니다.");
        }

        _storageAcl.EnsureProtectedDirectory(_recoveryRoot);
    }

    private static void EnsureFileIsNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "재분석 지점의 인증 파일은 사용할 수 없습니다.");
        }
    }

    private static void DeleteExpectedFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        EnsureFileIsNotReparsePoint(path);
        File.Delete(path);
    }

    private sealed record RecoveryState(bool HadCredential);
}

internal sealed record AntigravityCredentialBundle(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("keyringJson")] string KeyringJson,
    [property: JsonPropertyName("credentialUserScope")] string? CredentialUserScope,
    [property: JsonPropertyName("lastLoginEmail")] string? LastLoginEmail);
