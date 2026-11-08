using System.Diagnostics;
using AccountUsageUI.Core.Profiles;

namespace AccountUsageUI.Infrastructure.Providers.Antigravity;

public sealed class AntigravityProcessController : IAppProcessController
{
    private const string MainProcessName = "Antigravity";
    private const string LanguageServerProcessName = "language_server";

    private static readonly TimeSpan NormalStopTimeout =
        TimeSpan.FromSeconds(10);

    private readonly AntigravityInstallationLocator _installationLocator;

    public AntigravityProcessController(
        AntigravityInstallationLocator installationLocator)
    {
        _installationLocator = installationLocator;
    }

    public Task<bool> IsRunningAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = GetAntigravityMainProcesses();
        try
        {
            return Task.FromResult(processes.Count > 0);
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    public async Task<AppStopStatus> RequestStopAsync(
        CancellationToken cancellationToken)
    {
        var processes = GetAntigravityMainProcesses();

        try
        {
            if (processes.Count == 0)
            {
                return AppStopStatus.Stopped;
            }

            foreach (var process in processes)
            {
                try
                {
                    _ = process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // 확인 중 이미 종료된 프로세스다.
                }
            }

            var stopped = await WaitForStopAsync(
                NormalStopTimeout,
                cancellationToken);
            return stopped
                ? AppStopStatus.Stopped
                : AppStopStatus.ForceCloseRequired;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AppStopStatus.Failed;
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    public async Task<bool> ForceStopAsync(
        CancellationToken cancellationToken)
    {
        var processes = GetAllAntigravityProcesses();

        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 다른 프로세스를 종료하는 동안 이미 끝났다.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }

            return await WaitForStopAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    public Task<AppLaunchStatus> LaunchAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var executablePath = _installationLocator.FindExecutablePath();
            if (executablePath is null)
            {
                return Task.FromResult(AppLaunchStatus.InstallationNotFound);
            }

            _ = Process.Start(
                new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
                    UseShellExecute = true
                });
            return Task.FromResult(AppLaunchStatus.Launched);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Task.FromResult(AppLaunchStatus.Failed);
        }
    }

    public Task<bool> WaitForRunningAsync(
        CancellationToken cancellationToken)
    {
        return WaitForRunningCoreAsync(
            TimeSpan.FromSeconds(12),
            cancellationToken);
    }

    private static async Task<bool> WaitForRunningCoreAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = GetAntigravityMainProcesses();
            var running = processes.Count > 0;
            DisposeAll(processes);

            if (running)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        var remaining = GetAntigravityMainProcesses();
        var result = remaining.Count > 0;
        DisposeAll(remaining);
        return result;
    }

    private static async Task<bool> WaitForStopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = GetAntigravityMainProcesses();
            var stopped = processes.Count == 0;
            DisposeAll(processes);

            if (stopped)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        var remaining = GetAntigravityMainProcesses();
        var result = remaining.Count == 0;
        DisposeAll(remaining);
        return result;
    }

    private static List<Process> GetAntigravityMainProcesses()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcessesByName(MainProcessName))
        {
            if (IsAntigravityProcess(process))
            {
                matches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return matches;
    }

    private static List<Process> GetAllAntigravityProcesses()
    {
        var matches = GetAntigravityMainProcesses();
        foreach (var process in Process.GetProcessesByName(LanguageServerProcessName))
        {
            if (IsAntigravityLanguageServerProcess(process))
            {
                matches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return matches;
    }

    private static bool IsAntigravityProcess(Process process)
    {
        try
        {
            return IsAntigravityExecutablePath(process.MainModule?.FileName);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                  NotSupportedException or
                  System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool IsAntigravityLanguageServerProcess(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path) &&
                   path.Replace('/', '\\').Contains(
                       @"\Antigravity\resources\bin\language_server.exe",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                  NotSupportedException or
                  System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool IsAntigravityExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var normalized = executablePath.Replace('/', '\\');
        return normalized.EndsWith(
                   @"\Antigravity\Antigravity.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
