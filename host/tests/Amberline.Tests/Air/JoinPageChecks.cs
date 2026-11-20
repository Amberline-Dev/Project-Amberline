using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 918: the public Join page reads the invite after the #
/// of its own address (format v1). The static check reads the page and the export that publishes it; the decode check runs the
/// page's own script in Node.js when Node.js is installed. Batch 921: the page wears the sign-up sheet's look (its tokens are read
/// from HostPages.cs). No browser is started.
/// </summary>
public static class JoinPageChecks
{
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string Valid = "{\"v\":1,\"line\":\"132\",\"tag\":\"p132-v0.1.11\",\"host\":\"203.0.113.10\",\"port\":8391,\"pin\":\""
        + Pin + "\",\"name\":\"My Server\"}";
    private const string Downloads = "https://github.com/Amberline-Dev/Project-Amberline/releases/download/";
    private const string Damaged = "This invite link is damaged. Ask the host to copy it again.";
    private const string Newer = "This invite needs a newer Amberline Player. Download it from the invite page.";

    // The page's script runs without a document (so the page does not wire itself up) and prints each case's result, then the
    // wordmark with no invite (batch 921: the label and the mark of each line are pure functions of the invite).
    private const string DecodeDriver = """
        const vm = require("vm");
        const context = { atob, TextDecoder };
        vm.createContext(context);
        vm.runInContext(script, context);
        const results = inputs.map(function (input) {
          const result = context.decodeInvite(input);
          if (result.error) return { error: result.error };
          return { invite: result.invite, url: context.downloadUrl(result.invite), join: context.joinUrl(result.invite, result.encoded),
            label: context.downloadLabel(result.invite), mark: context.brandMark(result.invite) };
        });
        process.stdout.write(JSON.stringify({ results: results, unset: context.brandMark(null) }));
        """;


    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The custom properties of the first <c>:root</c> block in the text, by name without its dashes.</summary>
    private static Dictionary<string, string> RootTokens(string text)
    {
        var root = Regex.Match(text, @":root\{([^}]*)\}").Groups[1].Value;
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match token in Regex.Matches(root, @"--([a-z-]+):([^;]+)"))
            tokens[token.Groups[1].Value] = token.Groups[2].Value.Trim();
        return tokens;
    }

    /// <summary>Node.js's standard output for a script read from its standard input; null when Node.js is not installed.</summary>
    private static string? RunNode(string script)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo("node", "-")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Win32Exception)
        {
            return null;
        }
        if (process is null) return null;
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(script);
            process.StandardInput.Close();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("node did not finish the join page check");
            }
            TestAssert.Equal(0, process.ExitCode, error.Result);
            return output.Result;
        }
    }
}
