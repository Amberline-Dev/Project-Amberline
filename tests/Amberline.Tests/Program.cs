using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Amberline.Core;

namespace Amberline.Tests;

internal static class Program
{
    internal sealed record DiscoveredCheck(string Name, MethodInfo Method, bool Serial, double TimeoutSeconds = 0)
    {
        /// <summary>The run's <c>--timeout</c>, or the check's own <see cref="CheckAttribute.TimeoutSeconds"/> when that is longer.</summary>
        public double Timeout(double runTimeout) => Math.Max(runTimeout, TimeoutSeconds);
    }

    // Reflection over the whole assembly is the slowest part of runner start-up;
    // a worker or an in-process runner check discovers once.
    private static readonly Lazy<DiscoveredCheck[]> Discovered = new(Discover);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"runner error: {exception}");
            return 1;
        }
    }

    internal static int Run(string[] args)
    {
        var list = false;
        var listSerial = false;
        var listScopes = false;
        var worker = false;
        string? child = null;
        var timeout = 120.0;
        var parallel = 1;
        var selectors = new List<string>();
        var scopes = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument == "--list")
            {
                list = true;
            }
            else if (argument == "--list-serial")
            {
                listSerial = true;
            }
            else if (argument == "--list-scopes")
            {
                listScopes = true;
            }
            else if (argument == "--scope")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    Console.Error.WriteLine("--scope requires a scope name; use --list-scopes");
                    return 2;
                }
                scopes.Add(args[i]);
            }
            else if (argument == "--child")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    Console.Error.WriteLine("--child requires a check name");
                    return 2;
                }

                child = args[i];
            }
            else if (argument == "--worker")
            {
                worker = true;
            }
            else if (argument == "--parallel")
            {
                if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out parallel) || parallel < 1 || parallel > CheckScheduler.MaximumWorkers)
                {
                    Console.Error.WriteLine($"--parallel must be an integer from 1 to {CheckScheduler.MaximumWorkers}");
                    return 2;
                }
            }
            else if (argument == "--timeout")
            {
                if (++i >= args.Length || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) || !double.IsFinite(timeout) || timeout <= 0)
                {
                    Console.Error.WriteLine("--timeout must be finite and positive");
                    return 2;
                }
            }
            else if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"unknown option: {argument}");
                return 2;
            }
            else
            {
                selectors.Add(argument);
            }
        }

        if (listScopes)
        {
            if (list || listSerial || worker || child is not null || scopes.Count != 0 || selectors.Count != 0)
            { Console.Error.WriteLine("--list-scopes cannot be combined with check selection"); return 2; }
            foreach (var scope in CheckScopes.All.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                Console.WriteLine($"{scope.Key}: {string.Join(" ", scope.Value)}");
            return 0;
        }

        if (list && listSerial)
        { Console.Error.WriteLine("--list and --list-serial cannot be combined"); return 2; }

        var checks = Discovered.Value;
        if (worker)
        {
            if (list || listSerial || child is not null || selectors.Count != 0 || scopes.Count != 0)
            { Console.Error.WriteLine("--worker cannot be combined with --list, --child, scopes or selectors"); return 2; }
            return CheckWorker.Serve(checks);
        }

        if (child is not null)
        {
            if (list || listSerial || selectors.Count != 0 || scopes.Count != 0) { Console.Error.WriteLine("--child cannot be combined with --list, scopes or selectors"); return 2; }
            var check = checks.SingleOrDefault(x => string.Equals(x.Name, child, StringComparison.Ordinal));
            return check is null ? 2 : InvokeCheck(check);
        }

        foreach (var scope in scopes)
        {
            if (!CheckScopes.All.TryGetValue(scope, out var patterns))
            {
                Console.Error.WriteLine($"unknown scope: {scope}; use --list-scopes");
                return 2;
            }
            selectors.AddRange(patterns);
        }

        var unmatched = selectors.Where(selector => checks.All(check => !Matches(check.Name, selector))).ToArray();
        if (unmatched.Length > 0)
        {
            foreach (var selector in unmatched)
            {
                Console.Error.WriteLine($"no checks match {selector}");
            }

            return 2;
        }

        var selected = selectors.Count == 0
            ? checks
            : checks.Where(check => selectors.Any(selector => Matches(check.Name, selector))).ToArray();

        if (list || listSerial)
        {
            foreach (var check in selected.Where(check => list || check.Serial))
            {
                Console.WriteLine(check.Name);
            }

            return 0;
        }

        // Corpus checks write recorded fixtures and some read the output of an earlier
        // recording; the recorder is shared state, so recording stays one check at a time.
        if (parallel > 1 && FixtureRecorder.Enabled)
        {
            Console.Error.WriteLine("AMBERLINE_RECORD_FIXTURES=1: fixture recording runs serially; ignoring --parallel");
            parallel = 1;
        }

        if (parallel > 1)
        {
            return CheckScheduler.Run(selected, parallel, timeout);
        }

        var failed = RunIsolatedSequence(selected, timeout);
        Console.WriteLine($"{selected.Length - failed.Count}/{selected.Length} passed");
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>Runs each check in its own fresh child process, one at a time.</summary>
    internal static List<string> RunIsolatedSequence(IEnumerable<DiscoveredCheck> selected, double timeout)
    {
        var failed = new List<string>();
        foreach (var check in selected)
        {
            Console.Write($"RUN   {check.Name}");
            var start = Stopwatch.GetTimestamp();
            var result = RunIsolated(check.Name, check.Timeout(timeout));
            var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Console.WriteLine($"  {(result.ExitCode == 0 ? "PASS" : "FAIL")} ({elapsed.ToString("0.00", CultureInfo.InvariantCulture)}s)");
            if (result.ExitCode != 0)
            {
                failed.Add(check.Name);
                if (!string.IsNullOrWhiteSpace(result.Output)) Console.Error.WriteLine(result.Output.TrimEnd());
            }
        }

        return failed;
    }

    internal static int InvokeCheck(DiscoveredCheck check)
    {
        // DIAGNOSTICS (batch 297): every check draws from its own fixed seed (see SeededRandomSource.CheckSeed).
        Amberline.Core.SeededRandomSource.CheckSeed = CheckSeed(check.Name);
        try
        {
            check.Method.Invoke(null, null);
            return 0;
        }
        catch (TargetInvocationException exception)
        {
            Console.Error.WriteLine($"{check.Name}: {exception.InnerException ?? exception}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{check.Name}: {exception}");
            return 1;
        }
        finally
        {
            Amberline.Core.SeededRandomSource.CheckSeed = null;
        }
    }

    /// <summary>A check's fixed seed: its name hashed (SHA-256, not the per-process string hash), non-negative.</summary>
    internal static int CheckSeed(string name) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))) & int.MaxValue;

    /// <summary>A start-up description of this runner (`dotnet Amberline.Tests.dll` or the apphost) with the given arguments.</summary>
    internal static ProcessStartInfo RunnerStartInfo(params string[] arguments)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("test runner process path is unavailable");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static (int ExitCode, string Output) RunIsolated(string name, double timeoutSeconds)
    {
        using var process = new Process { StartInfo = RunnerStartInfo("--child", name) };
        if (!process.Start()) throw new InvalidOperationException("failed to start isolated check");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var milliseconds = timeoutSeconds >= int.MaxValue / 1000.0
            ? int.MaxValue
            : Math.Max(1, (int)Math.Ceiling(timeoutSeconds * 1000.0));
        if (!process.WaitForExit(milliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            return (1, $"Timed out after {timeoutSeconds.ToString("g", CultureInfo.InvariantCulture)}s\n{standardOutput.Result}{standardError.Result}");
        }

        Task.WaitAll(standardOutput, standardError);
        return (process.ExitCode, standardOutput.Result + standardError.Result);
    }

    private static DiscoveredCheck[] Discover()
    {
        var annotated = Assembly.GetExecutingAssembly()
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.Static | BindingFlags.Instance |
                                                 BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<CheckAttribute>()))
            .Where(item => item.Attribute is not null)
            .ToArray();

        var invalid = annotated.FirstOrDefault(item => !item.Method.IsPublic || !item.Method.IsStatic ||
                                                        item.Method.ReturnType != typeof(void) ||
                                                        item.Method.GetParameters().Length != 0);
        if (invalid.Attribute is not null)
        {
            throw new InvalidOperationException($"invalid [Check(\"{invalid.Attribute.Name}\")] method " +
                $"{invalid.Method.DeclaringType?.FullName}.{invalid.Method.Name}; " +
                "checks must be public static parameterless void methods");
        }

        var checks = annotated
            .Select(item => new DiscoveredCheck(item.Attribute!.Name, item.Method,
                CheckIsolation.IsSerial(item.Attribute.Name, item.Attribute.Serial), item.Attribute.TimeoutSeconds))
            .OrderBy(check => check.Name, StringComparer.Ordinal)
            .ThenBy(check => check.Method.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(check => check.Method.Name, StringComparer.Ordinal)
            .ToArray();

        if (checks.Length == 0)
        {
            throw new InvalidOperationException("no [Check] methods found");
        }

        var duplicate = checks.GroupBy(check => check.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"duplicate check name: {duplicate.Key}");
        }

        return checks;
    }

    internal static bool Matches(string value, string pattern)
    {
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, expression, RegexOptions.CultureInvariant);
    }
}
