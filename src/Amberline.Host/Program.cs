using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Amberline.Data;
using Amberline.Server;

namespace Amberline.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("AMBERLINE_HOST_HOME") ?? Path.Combine(AppContext.BaseDirectory, "host-data"));
            Directory.CreateDirectory(directory);
            var settingsPath = Path.Combine(directory, "host.json");
            if (args is ["--init", var dataRoot])
            {
                if (File.Exists(settingsPath)) throw new ArgumentException("Host settings already exist. Edit host.json to change them.");
                var settings = new HostSettings(Path.GetFullPath(dataRoot), Path.Combine(AppContext.BaseDirectory, "server", "Amberline.Server.exe"));
                settings.Validate();
                HostFiles.Write(settingsPath, settings);
                using var certificate = HostFiles.Certificate(directory);
                ExportGuest(directory, "Host administrator", settings.ControlPort, true);
                return 0;
            }
            if (!File.Exists(settingsPath)) throw new ArgumentException("Prepare the host with tools/package-remote.ps1 first.");
            var configuration = JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(settingsPath), HostFiles.Json)!;
            configuration.Validate();
            if (args is ["--revoke", var id])
            {
                var guests = HostFiles.ReadGuests(directory);
                if (!guests.Any(g => g.Id == id)) throw new ArgumentException("Guest ID not found.");
                HostFiles.Write(Path.Combine(directory, "guests.json"), guests.Where(g => g.Id != id).ToArray());
                Console.WriteLine("Invitation revoked. Existing game connections are not disconnected.");
                return 0;
            }
            if (args is ["--guests"])
            {
                foreach (var guest in HostFiles.ReadGuests(directory)) Console.WriteLine($"{guest.Id}  {guest.Name} {(guest.Admin ? "(administrator)" : "")}");
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Options: --init <game folder>, --guests, --revoke <guest ID>.");
            Environment.SetEnvironmentVariable("GAME_DATA_ROOT", configuration.DataRoot);
            using var tls = HostFiles.Certificate(directory);
            await using var manager = new GameManager(configuration, Path.Combine(directory, "games"));
            var catalog = new RemoteCatalog(1, ServerOptions.SupportedChampions.ToDictionary(c => c, c => GameData.Default.Skins(c).ToArray()),
                new RuneCatalog(GameData.Default.Paths).All.ToArray(), new MasteryCatalog(GameData.Default.Paths).Definitions.ToArray());
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 64 * 1024;
                options.Limits.MaxConcurrentConnections = 64;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Parse(configuration.BindAddress), configuration.ControlPort, listen => listen.UseHttps(tls));
            });
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
            await using var app = builder.Build();
            app.UseRateLimiter();
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var authorization = context.Request.Headers.Authorization.ToString();
                var guest = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                    ? HostFiles.Authenticate(directory, authorization[7..]) : null;
                if (guest is null) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Invitation is invalid or revoked." }); return; }
                context.Items["guest"] = guest;
                try { await next(context); }
                catch (RemoteFault ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
                catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
                catch (BadHttpRequestException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request body." }); }
            });
            static HostGuest Guest(HttpContext context) => (HostGuest)context.Items["guest"]!;
            // DIAGNOSTICS: a caller on this PC (loopback, not a remote address) may take the cheat seats of games it creates.
            static bool Local(HttpContext context) => context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
            app.MapGet("/api/v1/catalog", () => catalog);
            app.MapGet("/api/v1/games", (HttpContext context) => manager.List(Guest(context)));
            app.MapPost("/api/v1/games", (HttpContext context, CreateGameRequest request) => manager.Create(Guest(context), request, Local(context)));
            app.MapPost("/api/v1/games/{id:guid}/seats/{seat:int}/join", (HttpContext context, Guid id, int seat) => manager.Join(Guest(context), id, seat, Local(context)));
            app.MapPost("/api/v1/games/{id:guid}/stop", async (HttpContext context, Guid id) =>
            { await manager.StopAsync(Guest(context), id); return Results.Ok(); });
            Console.WriteLine($"Host ready. HTTPS TCP {configuration.ControlPort}; games UDP {configuration.FirstGamePort}–{configuration.FirstGamePort + configuration.MaxGames - 1}.");
            Console.WriteLine("Keep this window open. Ctrl+C stops only this manager's games. Use the administrator Launcher.exe to manage games.");
            if (Environment.GetEnvironmentVariable("AMBERLINE_HOST_CONTROL") == "1")
                _ = Task.Run(async () => { await Console.In.ReadLineAsync(); app.Lifetime.StopApplication(); });
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static void ExportGuest(string directory, string name, int port, bool admin = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl)) throw new ArgumentException("Guest name must contain 1–60 printable characters.");
        var template = Path.Combine(AppContext.BaseDirectory, "guest-template", "Launcher.exe");
        if (!File.Exists(template)) throw new FileNotFoundException("Guest Launcher.exe template is missing. Run package-remote.ps1.");
        var invitation = HostFiles.Invite(directory, name.Trim(), port, admin);
        var export = Path.Combine(directory, "invitations", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(export);
        File.Copy(template, Path.Combine(export, "Launcher.exe"));
        foreach (var nameToCopy in new[] { "BUILD.json", "NATIVE-CHECKLIST.md", "DIAGNOSTICS.md", "CHANGELOG.md" })
        {
            var source = Path.Combine(Path.GetDirectoryName(template)!, nameToCopy);
            if (File.Exists(source)) File.Copy(source, Path.Combine(export, nameToCopy));
        }
        HostFiles.Write(Path.Combine(export, "invitation.json"), invitation);
        File.WriteAllText(Path.Combine(export, "READ-ME.txt"), "Double-click Launcher.exe, enter the host IP, and connect.\nKeep invitation.json beside Launcher.exe and private. Select your existing patch-126 game folder when joining.\n");
        ZipFile.CreateFromDirectory(export, export + ".zip");
        Console.WriteLine($"{(admin ? "PRIVATE administrator launcher" : "Guest launcher for " + name)}: {export}.zip");
    }
}
