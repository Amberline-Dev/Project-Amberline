namespace Amberline.Tests;

/// <summary>
/// Which checks may share a long-lived worker process and run beside other checks.
/// Everything else is serial: it runs alone, after the parallel phase, in its own
/// fresh child process, exactly as the runner always ran checks.
/// </summary>
/// <remarks>
/// A check is serial when it touches anything outside its own process or anything it
/// cannot put back: a network port (native ENet host, TCP/UDP/HTTP listener), a
/// launched process, the AIR/host/launcher/server lifecycle, a fixed file path,
/// environment variables, the working directory or Console redirection. Mark a single
/// check with <c>[Check("name", Serial = true)]</c>; whole families go in
/// <see cref="SerialFamilies"/>. The workers also refuse to let a check leave changed
/// environment variables, working directory or Console writers behind, so an unmarked
/// check that does so fails loudly instead of leaking into the next one.
/// </remarks>
internal static class CheckIsolation
{
    /// <summary>
    /// Families that are serial as a whole.
    /// <c>server.*</c>: the AIR platform, host, launcher, remote bridge and native server
    /// lifecycle, which bind ports, launch processes and write the host's state files.
    /// <c>transport.*</c>: native ENet loopback hosts bind UDP ports.
    /// <c>tools.*</c>: offline CLI entry points redirect Console, read the workspace
    /// root and probe sockets.
    /// </summary>
    private static readonly string[] SerialFamilies = ["server.*", "transport.*", "tools.*"];

    public static IReadOnlyList<string> Families => SerialFamilies;

    public static bool IsSerial(string name, bool marked) =>
        marked || SerialFamilies.Any(pattern => Program.Matches(name, pattern));
}
