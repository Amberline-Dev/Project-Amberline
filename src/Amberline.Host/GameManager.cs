using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Server;

namespace Amberline.Host;

public sealed class RemoteFault(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>One immutable roster and owned process per match. Never discovers or kills other processes.</summary>
public sealed class GameManager : IAsyncDisposable
{
    private sealed class Game(Guid id, Guid request, HostGuest owner, MatchPlayer[] players, int port, string directory)
    {
        public Guid Id = id;
        public Guid Request = request;
        public HostGuest Owner = owner;
        public MatchPlayer[] Players = players;
        public int Port = port;
        /// <summary>The server's <c>--map-id</c> (1, 2, 3, 4 or 8), from the request.</summary>
        public int MapId = 1;
        /// <summary>The server's <c>--game-mode</c> when the request names one (batch 669: Battle Training, map 1 in TUTORIAL); null plays the map's own.</summary>
        public string? Mode;
        public string Directory = directory;
        public string Key = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 16);
        public string State = "Starting";
        public long? StoppedAt;
        public string? Error;
        // Keep child diagnostics for lifecycle failures only; stderr is not a public error channel.
        public readonly Queue<string> Diagnostics = [];
        public string? FailureDiagnostic;
        public Dictionary<int, string> Claims = [];
        public ManagedMatchStatus Status = new(false, false, [], []);
        public CancellationTokenSource Stop = new();
        public Task Runner = Task.CompletedTask;
        public TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Game> _games = [];
    private readonly List<RemoteGame> _history = [];
    private readonly Dictionary<string, DateTimeOffset> _lastCreate = [];
    private readonly HostSettings _settings;
    private readonly string _directory;
    private bool _closing;

    public GameManager(HostSettings settings, string directory)
    {
        settings.Validate();
        _settings = settings;
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "game.json", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
        {
            try
            {
                var game = JsonSerializer.Deserialize<RemoteGame>(File.ReadAllText(path), HostFiles.Json);
                if (game is not null && game.State != "Stopped")
                    _history.Add(game with { State = IsTerminal(game.State) ? game.State : "Interrupted", CanStop = false });
            }
            catch (Exception ex) when (ex is IOException or JsonException) { Console.Error.WriteLine($"Could not read match record: {path}"); }
        }
    }

    private static bool IsTerminal(string state) => state is "Stopped" or "Failed" or "Interrupted";

    private static bool IsFailureDiagnostic(string line) =>
        line.StartsWith("[E", StringComparison.Ordinal) ||
        line.StartsWith("Configuration error", StringComparison.Ordinal) ||
        line.StartsWith("Composition validation failed", StringComparison.Ordinal);

    private static void RecordDiagnostic(Game game, string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        if (line.Length > 500) line = line[..500];
        if (game.Diagnostics.Count == 8) game.Diagnostics.Dequeue();
        game.Diagnostics.Enqueue(line);
        if (IsFailureDiagnostic(line)) game.FailureDiagnostic = line;
    }

    private static string FailureMessage(Game game, string fallback) =>
        game.FailureDiagnostic ?? (fallback.Length > 500 ? fallback[..500] : fallback);

    private RemoteGame View(Game game, HostGuest guest) => new(game.Id, game.Owner.Name, game.State, game.Port,
        game.Players.Select((player, index) => new RemoteSeat(index + 1, player,
            game.Claims.ContainsKey(index + 1), game.Claims.GetValueOrDefault(index + 1) == guest.Id,
            game.Status.Connected.Contains(player.PlayerId ?? index + 1), game.Status.Loaded.Contains(player.PlayerId ?? index + 1))).ToArray(),
        !IsTerminal(game.State) && (guest.Admin || guest.Id == game.Owner.Id), game.Error);

    public RemoteGame[] List(HostGuest guest)
    {
        lock (_gate) return _games.Values.Reverse()
            .Where(g => g.StoppedAt is not long stoppedAt || Stopwatch.GetElapsedTime(stoppedAt) < TimeSpan.FromSeconds(5))
            .Select(g => View(g, guest)).Concat(_history).Take(200).ToArray();
    }

    /// <summary>
    /// DIAGNOSTICS: a game created on the host PC (<paramref name="local"/>: the caller reached the Host API over loopback, the AIR rule
    /// for the platform's sessions) gives its human seats the cheat commands; a game created from elsewhere gives none. Retail has no
    /// Host API. The platform's own games come through CreateReserved and keep the seat origin the platform recorded.
    /// </summary>
    public RemoteGame Create(HostGuest guest, CreateGameRequest request, bool local) => CreateCore(guest, WithCheatSeats(request, local), null);

    /// <summary>DIAGNOSTICS: the roster with the cheat rights <see cref="Create"/> gives; a CanCheat value sent by the client is never trusted.</summary>
    public static CreateGameRequest WithCheatSeats(CreateGameRequest request, bool local) =>
        request with { Players = request.Players.Select(player => player with { CanCheat = local && !player.IsBot }).ToArray() };

    /// <summary>DIAGNOSTICS: a seat that may cheat is claimed only from the host PC; a remote claim takes a seat without cheats.</summary>
    public static bool MayClaim(MatchPlayer player, bool local) => local || !player.CanCheat;

    /// <summary>
    /// The platform freezes account ownership with its final roster, before the process starts.
    /// <paramref name="participants"/> lists the human seats in roster order; bot seats are
    /// server-owned, hold no participant and cannot be claimed or joined.
    /// </summary>
    public RemoteGame CreateReserved(HostGuest guest, CreateGameRequest request, IReadOnlyList<HostGuest> participants,
        Action<Guid>? beforeStart = null)
    {
        var humanSeats = Enumerable.Range(1, request.Players.Length).Where(seat => !request.Players[seat - 1].IsBot).ToArray();
        if (humanSeats.Length == 0 || participants.Count != humanSeats.Length ||
            participants.Any(p => string.IsNullOrWhiteSpace(p.Id)) ||
            participants.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != participants.Count)
            throw new ArgumentException("Every platform human seat requires a distinct authenticated participant.");
        return CreateCore(guest, request, humanSeats.Zip(participants, (seat, participant) => (seat, participant.Id))
            .ToDictionary(pair => pair.seat, pair => pair.Id), beforeStart);
    }

    private RemoteGame CreateCore(HostGuest guest, CreateGameRequest request, IReadOnlyDictionary<int, string>? reservedOwners,
        Action<Guid>? beforeStart = null)
    {
        lock (_gate)
        {
            if (_closing) throw new RemoteFault(503, "The host is shutting down.");
            if (request.RequestId == Guid.Empty) throw new RemoteFault(400, "A request ID is required.");
            var existing = _games.Values.FirstOrDefault(g => g.Owner.Id == guest.Id && g.Request == request.RequestId);
            if (existing is not null) return View(existing, guest);
            if (_lastCreate.TryGetValue(guest.Id, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(3))
                throw new RemoteFault(429, "Please wait a few seconds before creating another game.");
            if (!Amberline.Core.GameMaps.IsSupported(request.MapId)) throw new RemoteFault(400, $"Select map {Amberline.Core.GameMaps.SupportedText}.");
            if (request.Mode is { } mode && !Amberline.Core.GameMaps.IsPlayedIn(request.MapId, mode)) throw new RemoteFault(400, $"Map {request.MapId} is not played in mode {mode}.");
            var players = MatchRoster.Validate(request.Players);
            var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(p => p.Port).ToHashSet();
            occupied.UnionWith(_games.Values.Where(g => !g.Runner.IsCompleted).Select(g => g.Port));
            var port = Enumerable.Range(_settings.FirstGamePort, _settings.MaxGames).FirstOrDefault(p => !occupied.Contains(p));
            if (port == 0) throw new RemoteFault(409, "All game ports are busy. Stop a game or wait for one to finish.");
            var id = Guid.NewGuid();
            var game = new Game(id, request.RequestId, guest, players, port, Path.Combine(_directory, id.ToString("N")))
            {
                MapId = request.MapId, Mode = request.Mode,
            };
            if (reservedOwners is not null)
                foreach (var (seat, owner) in reservedOwners) game.Claims.Add(seat, owner);
            Directory.CreateDirectory(game.Directory);
            File.WriteAllText(Path.Combine(game.Directory, "roster.json"), JsonSerializer.Serialize(players, MatchRoster.JsonOptions));
            Save(game);
            // The platform must durably associate accounts with this host ID
            // before a child can finish and publish a result.
            try { beforeStart?.Invoke(id); }
            catch
            {
                // The handoff failed before a process existed. Keep the roster
                // and an accurate terminal record for startup diagnostics.
                game.State = "Failed";
                game.Error = "Match handoff failed before server launch.";
                try { Save(game); }
                catch { /* Preserve the original handoff exception. */ }
                game.Stop.Dispose();
                throw;
            }
            _games.Add(id, game);
            _lastCreate[guest.Id] = DateTimeOffset.UtcNow;
            game.Runner = Task.Run(() => RunAsync(game));
            // ponytail: retain the last 200 completed requests in memory; disk records remain available to the operator.
            foreach (var old in _games.Values.Where(g => IsTerminal(g.State) && g.Runner.IsCompleted).Take(Math.Max(0, _games.Count - 200)).ToArray())
            { _games.Remove(old.Id); old.Stop.Dispose(); }
            return View(game, guest);
        }
    }

    public JoinGameReply Join(HostGuest guest, Guid id, int seat, bool local)
    {
        lock (_gate)
        {
            var game = Find(id);
            if (game.State is not ("Waiting for players" or "Playing")) throw new RemoteFault(409, "This game is not ready to join.");
            if (seat < 1 || seat > game.Players.Length || game.Players[seat - 1].IsBot) throw new RemoteFault(400, "Select a human seat.");
            var claim = game.Claims.GetValueOrDefault(seat);
            if (claim is not null && claim != guest.Id) throw new RemoteFault(409, "Another guest has claimed this seat.");
            if (claim is null && game.State == "Playing") throw new RemoteFault(409, "New seats cannot join after gameplay starts.");
            if (!MayClaim(game.Players[seat - 1], local)) throw new RemoteFault(403, "Only the host PC can claim this seat.");
            game.Claims[seat] = guest.Id;
            Save(game);
            return new JoinGameReply(game.Port, game.Players[seat - 1].PlayerId ?? seat, game.Key);
        }
    }

    /// <summary>Read only the authoritative result for a match this manager owns.</summary>
    public JsonElement? ReadResult(HostGuest guest, Guid id) => ReadCompletedResult(guest, id)?.Result;

    public (JsonElement Result, DateTimeOffset WrittenAt)? ReadCompletedResult(HostGuest guest, Guid id)
    {
        lock (_gate)
        {
            var game = Find(id);
            if (guest.Id != game.Owner.Id && !game.Claims.ContainsValue(guest.Id))
                throw new RemoteFault(403, "This account did not participate in this game.");
            // The result file is written before the child publishes its next
            // status pulse. A crash in that interval must not hide victory.
            return ReadSavedResult(id);
        }
    }

    /// <summary>Local startup recovery only. Live results use participant-authorized ReadResult.</summary>
    public (JsonElement Result, DateTimeOffset WrittenAt)? ReadArchivedResult(Guid id)
    {
        lock (_gate)
        {
            if (_games.ContainsKey(id)) throw new InvalidOperationException("This match still belongs to a running host.");
            return ReadSavedResult(id);
        }
    }

    private (JsonElement Result, DateTimeOffset WrittenAt)? ReadSavedResult(Guid id)
    {
        var logs = Path.Combine(_directory, id.ToString("N"), "logs");
        if (!Directory.Exists(logs)) return null;
        (JsonElement Result, DateTimeOffset WrittenAt)? found = null;
        foreach (var path in Directory.EnumerateFiles(logs, "match-result.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The final result envelope must be an object.");
            if (!document.RootElement.TryGetProperty("session_id", out var session) ||
                session.ValueKind != JsonValueKind.String || session.GetString() != id.ToString()) continue;
            if (found is not null) throw new InvalidDataException("Multiple final results exist for the same match.");
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The final result payload is missing.");
            // The stock-compatible server writes this file atomically at victory.
            // Preserve that write time when importing after a platform restart.
            found = (result.Clone(), new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
        }
        return found;
    }

    public async Task StopAsync(HostGuest guest, Guid id)
    {
        Task runner;
        lock (_gate)
        {
            var game = Find(id);
            if (!guest.Admin && guest.Id != game.Owner.Id) throw new RemoteFault(403, "Only the creator or host can stop this game.");
            if (!IsTerminal(game.State)) { game.State = "Stopping"; game.Stop.Cancel(); }
            runner = game.Runner;
        }
        await runner;
    }

    private Game Find(Guid id) => _games.GetValueOrDefault(id) ?? throw new RemoteFault(404, "Game not found on this host.");
    private void Save(Game game) => HostFiles.Write(Path.Combine(game.Directory, "game.json"),
        View(game, new HostGuest("", "", "")));

    private async Task RunAsync(Game game)
    {
        Process? process = null;
        var processStarted = false;
        try
        {
            var start = new ProcessStartInfo(_settings.ServerExecutable)
            {
                WorkingDirectory = Path.GetDirectoryName(_settings.ServerExecutable)!, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "--roster", Path.Combine(game.Directory, "roster.json"), "--data-root", _settings.DataRoot,
                         "--port", game.Port.ToString(), "--bind", _settings.BindAddress, "--speed", "1", "--log-dir",
                         Path.Combine(game.Directory, "logs"), "--launcher-control", "--managed-status", "--map-id", game.MapId.ToString() }) start.ArgumentList.Add(arg);
            if (game.Mode is { } gameMode) { start.ArgumentList.Add("--game-mode"); start.ArgumentList.Add(gameMode); }
            start.Environment["AMBERLINE_MATCH_KEY"] = game.Key;
            start.Environment["AMBERLINE_SESSION_ID"] = game.Id.ToString();
            start.Environment["GAME_DATA_ROOT"] = _settings.DataRoot;
            start.Environment.Remove("AMBERLINE_ENET_NATIVE");
            start.Environment["DEV_VISIBILITY_BASELINE"] = "0";
            start.Environment["MINION_FIDELITY_MODE"] = "0";
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == "launcher.ready") game.Ready.TrySetResult();
                if (args.Data?.StartsWith("host.status ", StringComparison.Ordinal) != true) return;
                try
                {
                    var status = JsonSerializer.Deserialize<ManagedMatchStatus>(args.Data[12..]);
                    if (status is null) return;
                    lock (_gate)
                    {
                        game.Status = status;
                        if (game.State is "Stopping" || IsTerminal(game.State)) return;
                        var state = status.Finished ? "Finished" : status.Started ? "Playing" : "Waiting for players";
                        if (game.State != state) { game.State = state; Save(game); }
                        if (status.Finished) game.Finished.TrySetResult();
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException) { Console.Error.WriteLine("Unable to record child status."); }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is { } line) lock (_gate) RecordDiagnostic(game, line);
            };
            game.Stop.Token.ThrowIfCancellationRequested();
            processStarted = process.Start();
            if (!processStarted) throw new IOException("Game process did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exit = process.WaitForExitAsync();
            await Task.WhenAny(game.Ready.Task, exit).WaitAsync(TimeSpan.FromSeconds(60), game.Stop.Token);
            if (!game.Ready.Task.IsCompletedSuccessfully || process.HasExited) throw new IOException("Game process exited before readiness. Check its host logs.");
            lock (_gate) { if (game.State == "Starting") game.State = "Waiting for players"; Save(game); }
            var startedAt = DateTimeOffset.UtcNow;
            while (!process.HasExited)
            {
                game.Stop.Token.ThrowIfCancellationRequested();
                if (game.Finished.Task.IsCompleted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_settings.FinishedGraceSeconds), game.Stop.Token);
                    break;
                }
                lock (_gate)
                {
                    var elapsed = DateTimeOffset.UtcNow - startedAt;
                    if ((!game.Status.Started && elapsed.TotalMinutes >= _settings.WaitingTimeoutMinutes) || elapsed.TotalHours >= _settings.MaxGameHours)
                    { game.Error = "Game stopped after the host time limit."; break; }
                }
                await Task.WhenAny(exit, Task.Delay(500, game.Stop.Token));
            }
            if (process.HasExited && !game.Stop.IsCancellationRequested && !game.Finished.Task.IsCompleted)
                throw new IOException("Game process exited unexpectedly. Check its host logs.");
        }
        catch (OperationCanceledException) when (game.Stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (processStarted && process is not null && process.HasExited)
            {
                try { process.WaitForExit(); }
                catch (InvalidOperationException) { }
            }
            lock (_gate)
            {
                var fallback = ex is TimeoutException ? "Game startup timed out." : ex.Message;
                game.Error = FailureMessage(game, fallback);
                game.State = "Failed";
            }
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (processStarted && !process.HasExited)
                    {
                        try
                        {
                            await process.StandardInput.WriteLineAsync("stop");
                            await process.StandardInput.FlushAsync();
                            process.StandardInput.Close();
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                        { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                    }
                }
                catch (InvalidOperationException) { /* Process.Start failed; no child to stop. */ }
                finally { process.Dispose(); }
            }
            lock (_gate)
            {
                if (game.State != "Failed")
                {
                    game.State = "Stopped";
                    game.StoppedAt = Stopwatch.GetTimestamp();
                }
                game.Key = "";
                Save(game);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate) { _closing = true; foreach (var g in _games.Values) g.Stop.Cancel(); pending = _games.Values.Select(g => g.Runner).ToArray(); }
        await Task.WhenAll(pending);
        lock (_gate) foreach (var g in _games.Values) g.Stop.Dispose();
    }
}
