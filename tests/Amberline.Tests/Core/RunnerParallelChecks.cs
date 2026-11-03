namespace Amberline.Tests;

/// <summary>
/// The parallel runner (--parallel N): option validation, which checks are serial,
/// and how a failing, leaking, hanging or crashing check is reported from a worker.
/// </summary>
public static class RunnerParallelChecks
{
    private const string ProbeVariable = "AMBERLINE_RUNNER_PROBE";

    /// <summary>Passes. The runner checks set <see cref="ProbeVariable"/> to make it misbehave inside a worker.</summary>
    [Check("runner.worker-probe")]
    public static void WorkerProbe()
    {
        switch (Environment.GetEnvironmentVariable(ProbeVariable))
        {
            case "fail":
                Console.WriteLine("probe console text");
                throw new InvalidOperationException("probe failure");
            case "leak":
                Environment.SetEnvironmentVariable("AMBERLINE_RUNNER_LEAK", "1");
                break;
            case "hang":
                Thread.Sleep(Timeout.Infinite);
                break;
            case "crash":
                Environment.Exit(3);
                break;
        }
    }

    [Check("runner.parallel-options", Serial = true)]
    public static void ParallelOptionsAreValidatedAndDoNotChangeSelection()
    {
        foreach (var args in new[] {
            new[] { "--parallel" }, new[] { "--parallel", "0" }, new[] { "--parallel", "-1" },
            new[] { "--parallel", "x" }, new[] { "--parallel", "2.5" }, new[] { "--parallel", "65" },
            new[] { "--list", "--list-serial" }, new[] { "--list-scopes", "--list-serial" },
            new[] { "--worker", "clock" }, new[] { "--worker", "--list" },
        })
        {
            var result = Run(args);
            TestAssert.Equal(2, result.Exit, string.Join(" ", args));
            TestAssert.True(result.Error.Length > 0, string.Join(" ", args));
            TestAssert.False(result.Output.Contains("RUN   "), "invalid options cannot run checks");
        }

        var all = Lines(Run("--list").Output);
        TestAssert.Equal(all, Lines(Run("--parallel", "4", "--list").Output), "--parallel does not change the selection");
        TestAssert.Equal(all, Lines(Run("--parallel", "64", "--list").Output));
        var core = Lines(Run("--scope", "core", "--list").Output);
        TestAssert.Equal(core, Lines(Run("--parallel", "3", "--scope", "core", "--list").Output));
    }


    private static (int Exit, string Output, string Error) Probe(string mode, string[] selection, params string[] extra)
    {
        var previous = Environment.GetEnvironmentVariable(ProbeVariable);
        Environment.SetEnvironmentVariable(ProbeVariable, mode);
        try { return Run(["--parallel", "2", .. extra, .. selection]); }
        finally { Environment.SetEnvironmentVariable(ProbeVariable, previous); }
    }

    private static string[] Passed(string output) => output.Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("RUN   ", StringComparison.Ordinal) && line.Contains("  PASS (", StringComparison.Ordinal))
        .Select(line => line["RUN   ".Length..line.IndexOf("  PASS (", StringComparison.Ordinal)])
        .Order(StringComparer.Ordinal).ToArray();

    private static string[] Lines(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static (int Exit, string Output, string Error) Run(params string[] args)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = Program.Run(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
