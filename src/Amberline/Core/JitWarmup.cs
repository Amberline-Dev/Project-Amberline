using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Amberline.Core;

/// <summary>What one warm-up compiled: the methods prepared, the methods that failed, the wall time and the failure list.</summary>
public sealed record JitWarmupResult(int Methods, int Failed, double Milliseconds, IReadOnlyList<string> Failures);

/// <summary>
/// NOT RETAIL: retail's server was native code, so a path's first use cost nothing extra. This server is .NET and compiles each
/// method on its first call: batch 423 measured a 99 ms first Maokai Q (546 methods compiled on the simulation thread), and batch
/// 425 measured 120 ms of compilation inside the first match step. The warm-up compiles the gameplay assembly's methods on a
/// background thread from server start. The first match step waits for it only while it still runs. Batch 573: the Release server is
/// also precompiled (ReadyToRun, Amberline.Server.csproj), so there the warm-up mostly loads precompiled code (about 0.14 s instead
/// of 2 s); it still compiles what the precompiled images lack, and everything in the JIT builds (tools, checks, Debug).
/// <see cref="RuntimeHelpers.PrepareMethod"/> only compiles: it runs no constructor and no gameplay code (batch 425 probe, .NET 10).
/// DIAGNOSTICS: AMBERLINE_JIT_WARMUP=0 turns it off, for before/after measurements.
/// </summary>
public static class JitWarmup
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly object Gate = new();
    private static Task<JitWarmupResult>? _task;
    private static int _awaited;

    /// <summary>Starts the warm-up once per process, on its own background thread. Off when AMBERLINE_JIT_WARMUP=0.</summary>
    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("AMBERLINE_JIT_WARMUP") == "0") return;
        lock (Gate)
            _task ??= Task.Factory.StartNew(() => Prepare(typeof(JitWarmup).Assembly),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// The first match step's wait: blocks only while the warm-up still runs. Once per process; later calls, and calls when no
    /// warm-up was started, return null.
    /// </summary>
    public static (JitWarmupResult Result, double WaitedMilliseconds)? AwaitFirstStep()
    {
        Task<JitWarmupResult>? task;
        lock (Gate) task = _task;
        if (task is null || Interlocked.Exchange(ref _awaited, 1) == 1) return null;
        var waited = Stopwatch.GetTimestamp();
        var result = task.GetAwaiter().GetResult();
        return (result, Stopwatch.GetElapsedTime(waited).TotalMilliseconds);
    }

    /// <summary>
    /// Compiles every non-abstract, non-generic method with a body that the assembly declares, constructors and static constructors
    /// included. Open generic types and methods, P/Invoke and internal calls are skipped. A method that fails to compile is counted
    /// and listed, never thrown.
    /// </summary>
    public static JitWarmupResult Prepare(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var started = Stopwatch.GetTimestamp();
        var failures = new List<string>();
        var methods = 0;
        foreach (var type in TypesOf(assembly, failures))
        {
            if (type.ContainsGenericParameters) continue;
            MethodBase[] members;
            try { members = [.. type.GetMethods(Declared), .. type.GetConstructors(Declared)]; }
            catch (Exception error) { failures.Add($"{type.FullName}: {error.GetType().Name}"); continue; }
            foreach (var member in members)
            {
                // Only IL bodies are compiled: runtime-provided members (a delegate's BeginInvoke and EndInvoke throw
                // PlatformNotSupportedException under PrepareMethod on .NET Core) and internal calls have none.
                if (member.IsAbstract || member.ContainsGenericParameters
                    || (member.Attributes & MethodAttributes.PinvokeImpl) != 0
                    || (member.MethodImplementationFlags & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.InternalCall))
                        != MethodImplAttributes.IL)
                    continue;
                try
                {
                    // Batch 554: on .NET 10 PrepareMethod compiles nothing for a virtual method (an interface implementation or an
                    // override) whose entry point was never requested, so those methods still compiled at their first call on the
                    // simulation thread (the JIT summary of S-Maokai's Q: MatchGameplayHooks.PreDamage, ProtocolCombatTransport.SendHealth,
                    // BbHookRouter.OnTakeDamage). Requesting the entry point first makes PrepareMethod compile it (check
                    // server.jit-warmup-compiles-virtual-methods).
                    if (member.IsVirtual) member.MethodHandle.GetFunctionPointer();
                    RuntimeHelpers.PrepareMethod(member.MethodHandle);
                    methods++;
                }
                catch (Exception error)
                {
                    failures.Add($"{type.FullName}.{member.Name}: {error.GetType().Name}");
                }
            }
        }
        return new JitWarmupResult(methods, failures.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds, failures);
    }

    private static Type[] TypesOf(Assembly assembly, List<string> failures)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error)
        {
            failures.Add($"{assembly.GetName().Name}: {error.LoaderExceptions.Length} types failed to load");
            return error.Types.OfType<Type>().ToArray();
        }
    }
}
