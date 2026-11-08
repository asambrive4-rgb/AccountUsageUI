using AccountUsageUI.Core.Installation;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public sealed class AntigravityInstallationLocator : IAppInstallationLocator
{
    private readonly string[] _candidateExecutablePaths;

    public AntigravityInstallationLocator()
        : this(GetDefaultCandidateExecutablePaths())
    {
    }

    public AntigravityInstallationLocator(
        IEnumerable<string> candidateExecutablePaths)
    {
        ArgumentNullException.ThrowIfNull(candidateExecutablePaths);
        _candidateExecutablePaths = candidateExecutablePaths.ToArray();
    }

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FindExecutablePath() is not null);
    }

    public string? FindExecutablePath()
    {
        foreach (var path in _candidateExecutablePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string[] GetDefaultCandidateExecutablePaths()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);

        return
        [
            Path.Combine(
                localAppData,
                "Programs",
                "Antigravity",
                "Antigravity.exe"),
            Path.Combine(
                programFiles,
                "Antigravity",
                "Antigravity.exe")
        ];
    }
}
