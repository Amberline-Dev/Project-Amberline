namespace Amberline.Server;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ServerOptions options;
        try
        {
            options = ServerOptions.Parse(args);
        }
        catch (ServerHelpRequestedException)
        {
            Console.WriteLine(ServerOptions.Usage());
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            Console.Error.WriteLine("Use --help for supported options.");
            return 2;
        }

        // The process default reads the match's map files (one match per process), before any match code runs: map 8's turrets, nexuses,
        // spawns, recall point and shop, and map 2's camps, come from it. It used to be set for --validate-only alone, so a real game on
        // map 8 ran on Summoner's Rift's statics (the owner's native Dominion test, 28 October 2026).
        if (options.MapId != 1) Amberline.Data.GameData.ConfigureDefault(new Amberline.Data.GameDataPaths(options.GameDataRoot).ForMap(options.MapId));

        if (options.ValidateOnly)
        {
            try
            {
                using var logger = ServerLogger.Open(options.LogDirectory,
                    clientLogDirectory: options.GameDataRoot);
                BuiltinServerMatchFactory.ValidateComposition(options, logger);
                logger.Flush();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Composition validation failed: {exception.Message}");
                return 1;
            }
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = null;
        handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        if (options.LauncherControl)
        {
            // The launcher owns this pipe. EOF also shuts down an orphaned server.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is { } command)
                        if (command.Equals("stop", StringComparison.OrdinalIgnoreCase)) break;
                    cancellation.Cancel();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            });
        }
        try
        {
            IServerMatchFactory factory = new BuiltinServerMatchFactory();
            return await ServerApplication.RunAsync(options, factory, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

}
