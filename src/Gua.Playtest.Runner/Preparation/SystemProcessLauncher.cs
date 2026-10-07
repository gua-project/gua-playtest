using System.Diagnostics;

namespace Gua.Playtest.Runner.Preparation;

/// <summary>No shell, discovery, guessed arguments, process-name lookup, or process-tree termination.</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public ValueTask<IOwnedProcess> LaunchAsync(LaunchCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // OS creation may block. Return promptly so the execution owner can enforce its real deadline.
        // The coordinator registers or releases the returned handle even if it arrives after cancellation.
        return new(Task.Run(() => Launch(command), CancellationToken.None));
    }
    private static IOwnedProcess Launch(LaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Path.IsPathFullyQualified(command.Executable) || !File.Exists(command.Executable) ||
            !Path.IsPathFullyQualified(command.WorkingDirectory) || !Directory.Exists(command.WorkingDirectory))
            throw new IOException("LaunchConfigurationUnavailable");
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        return new OwnedProcess(Process.Start(start) ?? throw new IOException("ProcessCreationFailed"));
    }
    private sealed class OwnedProcess(Process process) : IOwnedProcess
    {
        private bool disposed;
        public bool HasExited => disposed || process.HasExited;
        public ValueTask WaitForExitAsync(CancellationToken cancellationToken) => new(process.WaitForExitAsync(cancellationToken));
        public async ValueTask<bool> ShutdownAsync(CancellationToken cancellationToken)
        {
            if (disposed) return true;
            try
            {
                if (!process.HasExited)
                {
                    // Kill only the exact Process created above. Children need separately proven ownership.
                    process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!process.HasExited) return false;
                disposed = true; process.Dispose(); return true;
            }
            catch (InvalidOperationException) when (process.HasExited)
            { disposed = true; process.Dispose(); return true; }
        }
    }
}
