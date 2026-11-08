using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.Infrastructure.Profiles;

public sealed class WindowsProfileStore : IProfileStore
{
    private const int CurrentSchemaVersion = 1;
    private const string MetadataFileName = "profile.json";
    private const string CredentialFileName = "credential.bin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _storageRoot;
    private readonly CurrentUserDataProtector _protector = new();
    private readonly CurrentUserStorageAcl _storageAcl = new();

    public WindowsProfileStore()
        : this(GetDefaultStorageRoot())
    {
    }

    public WindowsProfileStore(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _storageRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(storageRoot));
    }

    public static string GetDefaultStorageRoot()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return Path.Combine(
            localAppData,
            "AccountUsageUI",
            "Profiles",
            "v1");
    }

    public async Task<ProfileStoreReadResult> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageRoot();

        var profiles = new List<Profile>();
        var issues = new List<ProfileStoreIssue>();

        foreach (var directory in Directory.EnumerateDirectories(
                     _storageRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith(
                    ".pending-",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var result = await TryReadProfileAsync(
                directory,
                cancellationToken);

            if (result.Profile is not null)
            {
                profiles.Add(result.Profile);
            }
            else if (result.Issue is not null)
            {
                issues.Add(result.Issue);
            }
        }

        return new ProfileStoreReadResult(
            profiles
                .OrderBy(
                    profile => profile.Name.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.Id.Value)
                .ToArray(),
            issues.ToArray());
    }

    public async Task SaveAsync(
        Profile profile,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageRoot();

        var destination = GetDirectChildPath(profile.Id);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidOperationException(
                "같은 ID의 프로필이 이미 저장되어 있습니다.");
        }

        var temporary = GetTemporaryDirectoryPath(profile.Id);
        _storageAcl.EnsureProtectedDirectory(temporary);

        try
        {
            var metadata = new ProfileMetadata(
                CurrentSchemaVersion,
                profile.Id.ToString(),
                profile.Name.Value);
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(
                metadata,
                JsonOptions);
            var protectedCredential = _protector.Protect(credential.Span);

            try
            {
                await WriteFileDurablyAsync(
                    Path.Combine(temporary, MetadataFileName),
                    metadataBytes,
                    cancellationToken);
                await WriteFileDurablyAsync(
                    Path.Combine(temporary, CredentialFileName),
                    protectedCredential,
                    cancellationToken);

                await VerifyStoredCredentialAsync(
                    Path.Combine(temporary, CredentialFileName),
                    credential,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(temporary, destination);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(metadataBytes);
                CryptographicOperations.ZeroMemory(protectedCredential);
            }
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(temporary);
        }
    }

    public async Task<byte[]> ReadCredentialAsync(
        ProfileId profileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageRoot();

        var directory = GetExistingSafeProfileDirectory(profileId);
        var credentialPath = Path.Combine(directory, CredentialFileName);
        var protectedCredential = await File.ReadAllBytesAsync(
            credentialPath,
            cancellationToken);

        try
        {
            return _protector.Unprotect(protectedCredential);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedCredential);
        }
    }

    public async Task ReplaceCredentialAsync(
        ProfileId profileId,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageRoot();

        var directory = GetExistingSafeProfileDirectory(profileId);
        var credentialPath = Path.Combine(directory, CredentialFileName);
        var protectedCredential = _protector.Protect(credential.Span);

        try
        {
            await AtomicFileWriter.WriteAsync(
                credentialPath,
                protectedCredential,
                cancellationToken);
            await VerifyStoredCredentialAsync(
                credentialPath,
                credential,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedCredential);
        }
    }

    public Task DeleteAsync(
        ProfileId profileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStorageRoot();

        var directory = GetDirectChildPath(profileId);
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        EnsureNotReparsePoint(directory);
        EnsureOnlyExpectedProfileFiles(directory);

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(directory, CredentialFileName));
        File.Delete(Path.Combine(directory, MetadataFileName));
        Directory.Delete(directory, recursive: false);
        return Task.CompletedTask;
    }

    private async Task<ProfileReadAttempt> TryReadProfileAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!IsDirectChild(directory) || IsReparsePoint(directory))
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.UnsafeStorageEntry);
        }

        var directoryName = Path.GetFileName(directory);
        if (!ProfileId.TryParse(directoryName, out var directoryProfileId))
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.CorruptMetadata);
        }

        ProfileMetadata? metadata;

        try
        {
            var metadataPath = Path.Combine(directory, MetadataFileName);
            if (File.Exists(metadataPath) && IsReparsePoint(metadataPath))
            {
                return ProfileReadAttempt.Failed(
                    ProfileStoreIssueCode.UnsafeStorageEntry);
            }

            await using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            metadata = await JsonSerializer.DeserializeAsync<ProfileMetadata>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  JsonException)
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.CorruptMetadata);
        }

        if (metadata is null ||
            metadata.SchemaVersion != CurrentSchemaVersion ||
            !ProfileId.TryParse(metadata.Id, out var metadataProfileId))
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.CorruptMetadata);
        }

        if (directoryProfileId != metadataProfileId)
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.ProfileIdMismatch);
        }

        ProfileName profileName;

        try
        {
            profileName = ProfileName.Create(metadata.Name);
        }
        catch (ArgumentException)
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.CorruptMetadata);
        }

        var credentialPath = Path.Combine(directory, CredentialFileName);
        if (!File.Exists(credentialPath))
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.MissingCredential);
        }

        if (IsReparsePoint(credentialPath))
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.UnsafeStorageEntry);
        }

        try
        {
            var protectedCredential = await File.ReadAllBytesAsync(
                credentialPath,
                cancellationToken);

            try
            {
                var plaintext = _protector.Unprotect(protectedCredential);
                CryptographicOperations.ZeroMemory(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedCredential);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidDataException or
                  CryptographicException or
                  System.ComponentModel.Win32Exception)
        {
            return ProfileReadAttempt.Failed(
                ProfileStoreIssueCode.UnreadableCredential);
        }

        return ProfileReadAttempt.Succeeded(
            new Profile(directoryProfileId, profileName));
    }

    private void EnsureStorageRoot()
    {
        if (Directory.Exists(_storageRoot))
        {
            EnsureNotReparsePoint(_storageRoot);
        }

        _storageAcl.EnsureProtectedDirectory(_storageRoot);
    }

    private string GetDirectChildPath(ProfileId profileId)
    {
        if (profileId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "프로필 ID는 비어 있을 수 없습니다.",
                nameof(profileId));
        }

        var path = Path.GetFullPath(
            Path.Combine(_storageRoot, profileId.ToString()));

        if (!IsDirectChild(path))
        {
            throw new InvalidOperationException(
                "프로필 저장 경로가 앱 관리 범위를 벗어났습니다.");
        }

        return path;
    }

    private string GetTemporaryDirectoryPath(ProfileId profileId)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                _storageRoot,
                $".pending-{profileId}-{Guid.NewGuid():N}"));

        if (!IsDirectChild(path))
        {
            throw new InvalidOperationException(
                "임시 저장 경로가 앱 관리 범위를 벗어났습니다.");
        }

        return path;
    }

    private bool IsDirectChild(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        var parent = Directory.GetParent(fullPath)?.FullName;

        return parent is not null &&
               StringComparer.OrdinalIgnoreCase.Equals(
                   Path.TrimEndingDirectorySeparator(
                       Path.GetFullPath(parent)),
                   _storageRoot);
    }

    private string GetExistingSafeProfileDirectory(ProfileId profileId)
    {
        var directory = GetDirectChildPath(profileId);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                "저장된 프로필을 찾을 수 없습니다.");
        }

        EnsureNotReparsePoint(directory);
        var credentialPath = Path.Combine(
            directory,
            CredentialFileName);
        if (File.Exists(credentialPath) &&
            IsReparsePoint(credentialPath))
        {
            throw new InvalidOperationException(
                "재분석 지점의 인증 사본은 읽을 수 없습니다.");
        }

        return directory;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new InvalidOperationException(
                "재분석 지점은 프로필 저장소로 사용할 수 없습니다.");
        }
    }

    private static void EnsureOnlyExpectedProfileFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(entry) || Directory.Exists(entry))
            {
                throw new InvalidOperationException(
                    "안전하지 않은 프로필 저장 항목이 발견되었습니다.");
            }

            var name = Path.GetFileName(entry);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    name,
                    MetadataFileName) &&
                !StringComparer.OrdinalIgnoreCase.Equals(
                    name,
                    CredentialFileName))
            {
                throw new InvalidOperationException(
                    "알 수 없는 프로필 저장 항목이 발견되었습니다.");
            }
        }
    }

    private static async Task WriteFileDurablyAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        await stream.WriteAsync(contents, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private async Task VerifyStoredCredentialAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        var protectedCredential = await File.ReadAllBytesAsync(
            path,
            cancellationToken);

        try
        {
            var plaintext = _protector.Unprotect(protectedCredential);

            try
            {
                if (plaintext.Length != expected.Length ||
                    !CryptographicOperations.FixedTimeEquals(
                        plaintext,
                        expected.Span))
                {
                    throw new IOException(
                        "저장된 인증 사본 검증에 실패했습니다.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedCredential);
        }
    }

    private static void DeleteOwnedTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
        {
            return;
        }

        foreach (var fileName in new[]
                 {
                     CredentialFileName,
                     MetadataFileName
                 })
        {
            var file = Path.Combine(path, fileName);
            if (File.Exists(file) && !IsReparsePoint(file))
            {
                File.Delete(file);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private sealed record ProfileMetadata(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record ProfileReadAttempt(
        Profile? Profile,
        ProfileStoreIssue? Issue)
    {
        public static ProfileReadAttempt Succeeded(Profile profile) =>
            new(profile, null);

        public static ProfileReadAttempt Failed(
            ProfileStoreIssueCode issueCode) =>
            new(null, new ProfileStoreIssue(issueCode));
    }
}
