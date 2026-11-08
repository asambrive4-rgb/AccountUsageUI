using System.Security.Cryptography;

namespace AccountUsageUI.Core.Profiles;

public sealed class CreateProfileLoginUseCase
{
    private readonly IProfileStore _profileStore;
    private readonly ProfileCreationValidator _validator;
    private readonly IAuthenticationSession _authenticationSession;
    private readonly IAppProcessController _processController;
    private readonly ProfileOperationCoordinator _operationCoordinator;
    private ProfileName? _pendingProfileName;

    public CreateProfileLoginUseCase(
        IProfileStore profileStore,
        IAuthenticationSession authenticationSession,
        IAppProcessController processController,
        ProfileOperationCoordinator? operationCoordinator = null)
    {
        _profileStore = profileStore;
        _validator = new ProfileCreationValidator(profileStore);
        _authenticationSession = authenticationSession;
        _processController = processController;
        _operationCoordinator =
            operationCoordinator ?? new ProfileOperationCoordinator();
    }

    public Task<bool> HasPendingRecoveryAsync(
        CancellationToken cancellationToken)
    {
        return _authenticationSession.HasPendingRecoveryAsync(
            cancellationToken);
    }

    /// <summary>
    /// 현재 로그인된 계정을 앱 재시작 없이 즉시 새 프로필로 캡처·등록한다.
    /// </summary>
    public async Task<CreateProfileLoginResult> CaptureCurrentAsync(
        string name,
        CancellationToken cancellationToken)
    {
        using var operation =
            await _operationCoordinator.TryEnterAsync(cancellationToken);
        if (operation is null)
        {
            return Failed();
        }

        byte[]? credential = null;

        try
        {
            if (_pendingProfileName is not null ||
                await _authenticationSession.HasPendingRecoveryAsync(
                    cancellationToken))
            {
                return Result(CreateProfileLoginStatus.RecoveryRequired);
            }

            var validation = await _validator.ValidateAsync(
                name,
                cancellationToken);
            var invalidResult = MapValidation(validation.Status);
            if (invalidResult is not null)
            {
                return invalidResult;
            }

            credential =
                await _authenticationSession.ReadCurrentCredentialAsync(
                    cancellationToken);
            if (credential is null)
            {
                return Result(CreateProfileLoginStatus.LoginNotCompleted);
            }

            var profile = new Profile(
                ProfileId.New(),
                validation.Name!);
            await _profileStore.SaveAsync(
                profile,
                credential,
                cancellationToken);

            return new CreateProfileLoginResult(
                CreateProfileLoginStatus.Created,
                profile);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failed();
        }
        finally
        {
            if (credential is not null)
            {
                CryptographicOperations.ZeroMemory(credential);
            }
        }
    }

    public async Task<CreateProfileLoginResult> StartAsync(
        string name,
        bool forceCloseApproved,
        CancellationToken cancellationToken)
    {
        using var operation =
            await _operationCoordinator.TryEnterAsync(cancellationToken);
        if (operation is null)
        {
            return Failed();
        }

        try
        {
            if (_pendingProfileName is not null ||
                await _authenticationSession.HasPendingRecoveryAsync(
                    cancellationToken))
            {
                return Result(CreateProfileLoginStatus.RecoveryRequired);
            }

            var validation = await _validator.ValidateAsync(
                name,
                cancellationToken);
            var invalidResult = MapValidation(validation.Status);
            if (invalidResult is not null)
            {
                return invalidResult;
            }

            var stopResult = await EnsureStoppedAsync(
                forceCloseApproved,
                cancellationToken);
            if (stopResult is not null)
            {
                return stopResult;
            }

            await _authenticationSession.PrepareForLoginAsync(
                cancellationToken);

            var launchStatus = await _processController.LaunchAsync(
                cancellationToken);
            if (launchStatus != AppLaunchStatus.Launched)
            {
                return await RestoreAfterFailureAsync(
                    launchStatus == AppLaunchStatus.InstallationNotFound
                        ? CreateProfileLoginStatus.InstallationNotFound
                        : CreateProfileLoginStatus.Failed,
                    cancellationToken);
            }

            _pendingProfileName = validation.Name;
            return Result(CreateProfileLoginStatus.WaitingForLogin);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await RestoreAfterFailureAsync(
                CreateProfileLoginStatus.Failed,
                CancellationToken.None);
        }
    }

    public async Task<CreateProfileLoginResult> CompleteAsync(
        bool forceCloseApproved,
        CancellationToken cancellationToken)
    {
        using var operation =
            await _operationCoordinator.TryEnterAsync(cancellationToken);
        if (operation is null)
        {
            return Failed();
        }

        byte[]? credential = null;

        try
        {
            if (_pendingProfileName is null)
            {
                return await _authenticationSession.HasPendingRecoveryAsync(
                        cancellationToken)
                    ? Result(CreateProfileLoginStatus.RecoveryRequired)
                    : Failed();
            }

            var stopResult = await EnsureStoppedAsync(
                forceCloseApproved,
                cancellationToken);
            if (stopResult is not null)
            {
                return stopResult;
            }

            credential =
                await _authenticationSession.ReadCurrentCredentialAsync(
                    cancellationToken);
            if (credential is null)
            {
                return await RestoreAndFinishAsync(
                    CreateProfileLoginStatus.LoginNotCompleted,
                    cancellationToken);
            }

            await _authenticationSession.RestorePreviousAsync(
                cancellationToken);
            await _authenticationSession.ClearRecoveryAsync(
                cancellationToken);

            var profile = new Profile(
                ProfileId.New(),
                _pendingProfileName);
            await _profileStore.SaveAsync(
                profile,
                credential,
                cancellationToken);
            _pendingProfileName = null;

            return new CreateProfileLoginResult(
                CreateProfileLoginStatus.Created,
                profile);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await RestoreAfterFailureAsync(
                CreateProfileLoginStatus.Failed,
                CancellationToken.None);
        }
        finally
        {
            if (credential is not null)
            {
                CryptographicOperations.ZeroMemory(credential);
            }
        }
    }

    public async Task<CreateProfileLoginResult> CancelAsync(
        bool forceCloseApproved,
        CancellationToken cancellationToken)
    {
        using var operation =
            await _operationCoordinator.TryEnterAsync(cancellationToken);
        if (operation is null)
        {
            return Failed();
        }

        try
        {
            if (!await _authenticationSession.HasPendingRecoveryAsync(
                    cancellationToken))
            {
                _pendingProfileName = null;
                return Result(CreateProfileLoginStatus.Canceled);
            }

            var stopResult = await EnsureStoppedAsync(
                forceCloseApproved,
                cancellationToken);
            if (stopResult is not null)
            {
                return stopResult;
            }

            return await RestoreAndFinishAsync(
                CreateProfileLoginStatus.Canceled,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(CreateProfileLoginStatus.RecoveryRequired);
        }
    }

    private async Task<CreateProfileLoginResult?> EnsureStoppedAsync(
        bool forceCloseApproved,
        CancellationToken cancellationToken)
    {
        var status = await _processController.RequestStopAsync(
            cancellationToken);
        if (status == AppStopStatus.Stopped)
        {
            return null;
        }

        if (status == AppStopStatus.Failed)
        {
            return Failed();
        }

        if (!forceCloseApproved)
        {
            return Result(
                CreateProfileLoginStatus.ForceCloseConfirmationRequired);
        }

        return await _processController.ForceStopAsync(cancellationToken)
            ? null
            : Failed();
    }

    private async Task<CreateProfileLoginResult> RestoreAndFinishAsync(
        CreateProfileLoginStatus finalStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            await _authenticationSession.RestorePreviousAsync(
                cancellationToken);
            await _authenticationSession.ClearRecoveryAsync(
                cancellationToken);
            _pendingProfileName = null;
            return Result(finalStatus);
        }
        catch
        {
            return Result(CreateProfileLoginStatus.RecoveryRequired);
        }
    }

    private async Task<CreateProfileLoginResult> RestoreAfterFailureAsync(
        CreateProfileLoginStatus finalStatus,
        CancellationToken cancellationToken)
    {
        if (!await _authenticationSession.HasPendingRecoveryAsync(
                cancellationToken))
        {
            _pendingProfileName = null;
            return Result(finalStatus);
        }

        var restored = await RestoreAndFinishAsync(
            finalStatus,
            cancellationToken);
        return restored.Status == CreateProfileLoginStatus.RecoveryRequired
            ? restored
            : Result(finalStatus);
    }

    private static CreateProfileLoginResult? MapValidation(
        ProfileCreationValidationStatus status)
    {
        return status switch
        {
            ProfileCreationValidationStatus.Valid => null,
            ProfileCreationValidationStatus.InvalidName =>
                Result(CreateProfileLoginStatus.InvalidName),
            ProfileCreationValidationStatus.DuplicateName =>
                Result(CreateProfileLoginStatus.DuplicateName),
            _ => Result(CreateProfileLoginStatus.StorageNeedsAttention)
        };
    }

    private static CreateProfileLoginResult Result(
        CreateProfileLoginStatus status) =>
        new(status);

    private static CreateProfileLoginResult Failed() =>
        Result(CreateProfileLoginStatus.Failed);
}
