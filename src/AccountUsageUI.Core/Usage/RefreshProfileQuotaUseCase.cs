using System.Security.Cryptography;
using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.Core.Usage;

public sealed class RefreshProfileQuotaUseCase
{
    private readonly IProfileStore _profileStore;
    private readonly IProfileQuotaReader _reader;
    private readonly ProfileOperationCoordinator _operationCoordinator;

    public RefreshProfileQuotaUseCase(
        IProfileStore profileStore,
        IProfileQuotaReader reader,
        ProfileOperationCoordinator operationCoordinator)
    {
        _profileStore = profileStore;
        _reader = reader;
        _operationCoordinator = operationCoordinator;
    }

    public async Task<ProfileQuotaSnapshot> ExecuteAsync(
        ProfileId profileId,
        bool isActiveProfile,
        CancellationToken cancellationToken)
    {
        using var operation =
            await _operationCoordinator.TryEnterAsync(cancellationToken);
        if (operation is null)
        {
            return Failed(profileId);
        }

        byte[]? credential = null;
        byte[]? refreshedCredential = null;

        try
        {
            credential = await _profileStore.ReadCredentialAsync(
                profileId,
                cancellationToken);
            var result = await _reader.ReadAsync(
                profileId,
                credential,
                isActiveProfile,
                cancellationToken);
            refreshedCredential = result.RefreshedCredential;

            if (result.Status == ProfileQuotaStatus.Available &&
                refreshedCredential is not null &&
                !CredentialsEqual(credential, refreshedCredential))
            {
                await _profileStore.ReplaceCredentialAsync(
                    profileId,
                    refreshedCredential,
                    cancellationToken);
            }

            return new ProfileQuotaSnapshot(
                profileId,
                result.Status,
                result.Models,
                result.AccountEmail,
                result.AccountPlan,
                result.LastSelectedModelId,
                result.MostRecentlyUsedModelId,
                result.Status == ProfileQuotaStatus.Available
                    ? DateTimeOffset.Now
                    : null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            return new ProfileQuotaSnapshot(
                profileId,
                ProfileQuotaStatus.UnsupportedAuthentication);
        }
        catch (DirectoryNotFoundException)
        {
            return Failed(profileId);
        }
        catch (FileNotFoundException)
        {
            return Failed(profileId);
        }
        catch
        {
            return Failed(profileId);
        }
        finally
        {
            if (credential is not null)
            {
                CryptographicOperations.ZeroMemory(credential);
            }

            if (refreshedCredential is not null)
            {
                CryptographicOperations.ZeroMemory(refreshedCredential);
            }
        }
    }

    private static bool CredentialsEqual(
        byte[] expected,
        byte[] actual)
    {
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expected,
                   actual);
    }

    private static ProfileQuotaSnapshot Failed(ProfileId profileId) =>
        new(profileId, ProfileQuotaStatus.Failed);
}
