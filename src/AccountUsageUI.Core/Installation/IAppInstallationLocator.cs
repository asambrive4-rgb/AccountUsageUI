namespace AccountUsageUI.Core.Installation;

public interface IAppInstallationLocator
{
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken);
}
