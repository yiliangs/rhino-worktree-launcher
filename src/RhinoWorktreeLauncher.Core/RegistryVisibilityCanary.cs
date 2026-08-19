using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// Proves that a current-user registry write this machine just made is visible to a process
// that did not make it, before anything depends on it.
//
// A launcher host can run inside a per-process sandbox that intercepts its current-user
// writes: the writing process reads its own key back and sees it, while the real hive never
// receives it. Rhino then loads nothing and the launch can only reach its timeout. Reading
// the key back in the writing process cannot detect that, so the reader here is always a
// separate process (ADR 0015).
internal static class RegistryVisibilityCanary
{
    // RWL's own key. The canary never writes a probe value under a McNeel key that is not
    // already part of the launch's own seed.
    private const string CanaryKeyPath = @"Software\RhinoWorktreeLauncher\canary";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    // Writes a nonce, reads it back through the probe, and removes it. The probe must be
    // spawned the way the sandbox boundary requires: interactively when this process may
    // itself be sandboxed, directly when it is already outside one.
    public static async Task<RegistryVisibility> VerifyAsync(
        RegistryProbeRunner probe,
        bool spawnInteractively,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Registry visibility checks require Windows.");

        string nonce = Guid.NewGuid().ToString("N");
        string keyPath = $@"{CanaryKeyPath}\{nonce}";
        WriteNonce(keyPath, nonce);
        try
        {
            RegistryProbeResult observed = await probe(
                new RegistryProbeRequest
                {
                    Hive = RegistryHives.CurrentUser,
                    KeyPath = keyPath,
                    Values = new[] { NonceValue }
                },
                spawnInteractively,
                cancellationToken);
            string? read = observed.Value(NonceValue);
            return new RegistryVisibility(
                string.Equals(read, nonce, StringComparison.Ordinal),
                nonce,
                read,
                observed.Error,
                $@"HKEY_CURRENT_USER\{keyPath}");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    // The launch's own check: the install seed Rhino is about to read must be visible to a
    // process other than the one that wrote it, nonce included. The nonce is what
    // distinguishes this launch's seed from an identical one an earlier launch left in the
    // real hive.
    public static async Task<RegistryVisibility> VerifySeedAsync(
        PluginSeed seed,
        RegistryProbeRunner probe,
        bool spawnInteractively,
        CancellationToken cancellationToken)
    {
        RegistryProbeResult observed = await probe(
            new RegistryProbeRequest
            {
                Hive = seed.Hive,
                KeyPath = seed.KeyPath,
                Values = new[] { "FileName", NonceValue }
            },
            spawnInteractively,
            cancellationToken);
        bool visible = string.Equals(observed.Value(NonceValue), seed.Nonce, StringComparison.Ordinal) &&
            string.Equals(observed.Value("FileName"), seed.FileName, StringComparison.OrdinalIgnoreCase);
        return new RegistryVisibility(
            visible,
            seed.Nonce,
            observed.Value(NonceValue),
            observed.Error,
            $@"{HiveName(seed.Hive)}\{seed.KeyPath}");
    }

    public const string NonceValue = "RwlLaunchNonce";

    [SupportedOSPlatform("windows")]
    private static void WriteNonce(string keyPath, string nonce)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true) ??
            throw new LaunchDiagnosticException(
                LaunchExecutorCodes.RegistrySeedNotVisible,
                $@"RWL cannot create its own key 'HKEY_CURRENT_USER\{keyPath}'.");
        key.SetValue(NonceValue, nonce, RegistryValueKind.String);
    }

    private static string HiveName(string hive) => hive == RegistryHives.LocalMachine
        ? "HKEY_LOCAL_MACHINE"
        : "HKEY_CURRENT_USER";
}

// What an independent reader saw. Visible is the only thing a caller may act on; the rest
// is what the diagnostic says when it did not.
internal sealed record RegistryVisibility(
    bool Visible,
    string Expected,
    string? Observed,
    string? ProbeError,
    string KeyPath)
{
    public string Describe() => ProbeError is not null
        ? $"An independent process could not read '{KeyPath}': {ProbeError}"
        : Observed is null
            ? $"An independent process cannot see '{KeyPath}', which this process just wrote. " +
                "The write did not reach the registry Rhino reads, so this process is running " +
                "with its current-user registry writes intercepted. Launch RWL from an " +
                "ordinary shell, or from the desktop application, instead of from inside " +
                "the client that sandboxes it."
            : $"An independent process reads '{Observed}' from '{KeyPath}' where this process " +
                $"wrote '{Expected}'.";
}

internal delegate Task<RegistryProbeResult> RegistryProbeRunner(
    RegistryProbeRequest request,
    bool spawnInteractively,
    CancellationToken cancellationToken);

// Runs the probe as a separate process over a private pipe. Interactive spawning goes
// through the Windows shell, which is what puts the reader outside a sandbox this process
// may be inside; direct spawning is for callers that are already outside one.
internal static class BootstrapRegistryProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static async Task<RegistryProbeResult> RunAsync(
        RegistryProbeRequest request,
        bool spawnInteractively,
        CancellationToken cancellationToken)
    {
        string bootstrapPath = InteractiveProcessSpawner.ResolveBootstrapPath();
        string pipeName = $"rwl-probe-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using IDisposable spawned = spawnInteractively
            ? InteractiveProcessSpawner.Spawn(bootstrapPath, $"registry-probe --pipe {pipeName}")
            : StartDirectly(bootstrapPath, pipeName);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(RegistryProbeProtocol.SerializeRequest(request))
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return line is null
                ? new RegistryProbeResult { Error = "The registry probe ended without answering." }
                : RegistryProbeProtocol.DeserializeResult(line) ??
                    new RegistryProbeResult { Error = $"The registry probe answered '{line}'." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RegistryProbeResult
            {
                Error = $"No registry probe answered within {ConnectTimeout.TotalSeconds:0.###} seconds."
            };
        }
    }

    private static IDisposable StartDirectly(string bootstrapPath, string pipeName)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = bootstrapPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("registry-probe");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        return Process.Start(startInfo) ??
            throw new LaunchDiagnosticException(
                LaunchExecutorCodes.RegistryProbeFailed,
                $"Windows did not start the registry probe '{bootstrapPath}'.");
    }
}
