namespace AccountUsageUI.Core.Profiles;

public enum ProfileCreationValidationStatus
{
    Valid,
    InvalidName,
    DuplicateName,
    StorageNeedsAttention
}

public sealed record ProfileCreationValidationResult(
    ProfileCreationValidationStatus Status,
    ProfileName? Name = null);

public enum CreateProfileLoginStatus
{
    WaitingForLogin,
    Created,
    Canceled,
    InvalidName,
    DuplicateName,
    StorageNeedsAttention,
    LoginNotCompleted,
    InstallationNotFound,
    ForceCloseConfirmationRequired,
    RecoveryRequired,
    Failed
}

public sealed record CreateProfileLoginResult(
    CreateProfileLoginStatus Status,
    Profile? Profile = null);

public enum RunProfileStatus
{
    Running,
    AlreadyRunning,
    ProfileNotFound,
    InstallationNotFound,
    LaunchFailed,
    AuthenticationMismatch,
    RecoveryRequired,
    Failed
}

public sealed record RunProfileResult(RunProfileStatus Status);

public enum SwitchProfileStatus
{
    Switched,
    AlreadyRunningTarget,
    ProfileNotFound,
    RunningUnknownProfile,
    InstallationNotFound,
    LaunchFailed,
    AuthenticationMismatch,
    RecoveryRequired,
    Failed
}

public sealed record SwitchProfileResult(SwitchProfileStatus Status);

public enum DeleteProfileStatus
{
    Deleted,
    ProfileNotFound,
    ActiveProfileBlocked,
    RunningProfileUnknown,
    RecoveryRequired,
    Failed
}

public sealed record DeleteProfileResult(DeleteProfileStatus Status);

public enum ProfileRuntimeStatus
{
    Stopped,
    RunningKnownProfile,
    RunningUnknownProfile
}

public sealed record ProfileRuntimeState(
    ProfileRuntimeStatus Status,
    ProfileId? ActiveProfileId = null);
