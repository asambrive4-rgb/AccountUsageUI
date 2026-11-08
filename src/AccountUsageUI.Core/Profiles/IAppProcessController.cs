namespace AccountUsageUI.Core.Profiles;

public interface IAppProcessController
{
    Task<bool> IsRunningAsync(
        CancellationToken cancellationToken);

    Task<AppStopStatus> RequestStopAsync(
        CancellationToken cancellationToken);

    Task<bool> ForceStopAsync(
        CancellationToken cancellationToken);

    Task<AppLaunchStatus> LaunchAsync(
        CancellationToken cancellationToken);

    Task<bool> WaitForRunningAsync(
        CancellationToken cancellationToken);
}
