namespace AccountUsageUI.Core.Profiles;

public enum AppLaunchStatus
{
    Launched,
    InstallationNotFound,
    Failed
}

public enum AppStopStatus
{
    Stopped,
    ForceCloseRequired,
    Failed
}
