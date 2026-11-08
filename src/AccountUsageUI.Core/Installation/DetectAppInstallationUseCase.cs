namespace AccountUsageUI.Core.Installation;

public sealed class DetectAppInstallationUseCase
{
    private readonly IAppInstallationLocator _installationLocator;

    public DetectAppInstallationUseCase(
        IAppInstallationLocator installationLocator)
    {
        _installationLocator = installationLocator;
    }

    public async Task<DetectAppInstallationResult> ExecuteAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var isInstalled = await _installationLocator.IsInstalledAsync(
                cancellationToken);
            return new DetectAppInstallationResult(
                isInstalled
                    ? AppInstallationDetectionStatus.Installed
                    : AppInstallationDetectionStatus.NotInstalled);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new DetectAppInstallationResult(
                AppInstallationDetectionStatus.Failed);
        }
    }
}
