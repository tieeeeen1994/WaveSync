using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace WaveSync.WaveLink;

/// <summary>
/// Client for Wave Link 3's local WebSocket JSON-RPC API (the one the official Stream Deck plugin uses).
/// Keeps reconnecting in the background, so Wave Link can be started or restarted at any time.
///
/// Protocol notes (reverse-engineered by the community, not an official Elgato spec):
///   • ws://127.0.0.1:&lt;port&gt;, port from %LOCALAPPDATA%\Packages\Elgato.WaveLink_*\LocalState\ws-info.json, else 1884–1893
///   • Origin: streamdeck://
///   • getApplicationInfo → { appID: "EWL", ... }
///   • getOutputDevices → { mainOutput: { outputDeviceId, outputId }, outputDevices: [ { id, name, outputs: [ { id, name } ] } ] }
///   • setOutputDevice { mainOutput: { outputDeviceId, outputId } }
///   • notification outputDevicesChanged (same shape as getOutputDevices)
/// </summary>
internal sealed class WaveLinkClient : IDisposable
{
    private static readonly int[] FallbackPorts = Enumerable.Range(1884, 10).ToArray();

    private readonly SynchronizationContext _ui;
    private readonly Func<AppConfig> _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _stateGate = new();

    private volatile ClientWebSocket? _ws;
    private WaveLinkState? _latest; // background copy, guarded by _stateGate
    private string? _signature;     // guarded by _stateGate
    private int _nextId;
    private int _refreshQueued;

    public WaveLinkClient(SynchronizationContext ui, Func<AppConfig> config)
    {
        _ui = ui;
        _config = config;
    }

    // These three are only written on the UI thread (via Post), so the UI can read them freely.
    public bool IsConnected { get; private set; }
    public int Port { get; private set; }
    public WaveLinkState? State { get; private set; }

    /// <summary>Raised on the UI thread.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Raised on the UI thread when the main output or the list of outputs changes.</summary>
    public event Action<WaveLinkState>? StateChanged;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public async Task SetMainOutputAsync(WaveLinkOutput output)
    {
        await RequestAsync("setOutputDevice", new
        {
            mainOutput = new { outputDeviceId = output.DeviceId, outputId = output.OutputId },
        }).ConfigureAwait(false);

        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task RefreshAsync()
    {
        var result = await RequestAsync("getOutputDevices", null).ConfigureAwait(false);
        var state = ParseState(result);
        if (state is not null)
        {
            Publish(state);
        }
    }

    // ───────────────────────── connection lifecycle ─────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var port in CandidatePorts())
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                bool hadSession;
                try
                {
                    hadSession = await RunSessionAsync(port, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"Wave Link session on port {port} failed", ex);
                    hadSession = false;
                }

                if (hadSession)
                {
                    break; // it was Wave Link and it went away; rediscover from scratch after the delay
                }
            }

            try
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>true if this port turned out to be Wave Link (and the session has now ended).</returns>
    private async Task<bool> RunSessionAsync(int port, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "streamdeck://");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return false; // nothing listening, or not a WebSocket server
        }

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws = ws;
        var receive = ReceiveLoopAsync(ws, sessionCts.Token);
        var validated = false;

        try
        {
            var info = await RequestAsync("getApplicationInfo", null, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (!IsWaveLink(info))
            {
                var raw = info.ValueKind == JsonValueKind.Undefined ? "(empty)" : info.GetRawText();
                Log.Info($"Port {port} answered but isn't Wave Link: {raw}");
                return false;
            }

            validated = true;
            Log.Info($"Connected to Wave Link on port {port}: {Json.Str(info, "name")} " +
                     $"{Json.Str(info, "version")} (interface rev {Json.Str(info, "interfaceRevision")})");
            Post(() =>
            {
                Port = port;
                IsConnected = true;
                ConnectionChanged?.Invoke();
            });

            await RefreshAsync().ConfigureAwait(false);

            while (!sessionCts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var pollMs = _config().WaveLinkPollMs;
                var delay = Task.Delay(pollMs > 0 ? pollMs : Timeout.Infinite, sessionCts.Token);
                if (await Task.WhenAny(delay, receive).ConfigureAwait(false) == receive)
                {
                    break;
                }

                try
                {
                    await RefreshAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Wave Link poll failed", ex);
                }
            }

            Log.Info("Wave Link connection closed");
            return true;
        }
        catch (Exception ex)
        {
            if (validated)
            {
                Log.Error("Wave Link connection lost", ex);
            }

            return validated;
        }
        finally
        {
            sessionCts.Cancel();
            _ws = null;
            FailPending();
            lock (_stateGate)
            {
                _latest = null;
                _signature = null;
            }

            if (validated)
            {
                Post(() =>
                {
                    IsConnected = false;
                    State = null;
                    ConnectionChanged?.Invoke();
                });
            }

            try { ws.Abort(); } catch { /* already closed */ }
            try { await receive.ConfigureAwait(false); } catch { /* expected on abort */ }
        }
    }

    private static bool IsWaveLink(JsonElement info)
    {
        var appId = Json.Str(info, "appID");
        var name = Json.Str(info, "name") ?? "";
        return string.Equals(appId, "EWL", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Wave Link", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<int> CandidatePorts()
    {
        var configured = _config().WaveLinkPort;
        if (configured > 0)
        {
            yield return configured;
            yield break;
        }

        var seen = new HashSet<int>();
        foreach (var port in ReadPortFiles())
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }

        foreach (var port in FallbackPorts)
        {
            if (seen.Add(port))
            {
                yield return port;
            }
        }
    }

    private static IEnumerable<int> ReadPortFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var files = new List<string>();

        try
        {
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.EnumerateDirectories(packages, "Elgato.WaveLink*"))
                {
                    files.Add(Path.Combine(dir, "LocalState", "ws-info.json"));
                }
            }
        }
        catch
        {
            // ignore unreadable folders
        }

        files.Add(Path.Combine(roaming, "Elgato", "WaveLink", "ws-info.json"));
        files.Add(Path.Combine(local, "Elgato", "WaveLink", "ws-info.json"));

        foreach (var file in files)
        {
            var port = 0;
            try
            {
                if (File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (int.TryParse(Json.Str(doc.RootElement, "port"), out var parsed))
                    {
                        port = parsed;
                    }
                }
            }
            catch
            {
                // malformed or locked file; fall through to the port scan
            }

            if (port > 0)
            {
                yield return port;
            }
        }
    }

    // ───────────────────────── JSON-RPC plumbing ─────────────────────────

    private async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan? timeout = null)
    {
        var ws = _ws ?? throw new InvalidOperationException("Wave Link is not connected.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        });

        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(new TimeoutException($"Wave Link didn't answer '{method}'."));
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            try
            {
                HandleMessage(message.ToArray());
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't handle a message from Wave Link", ex);
            }
        }
    }

    private void HandleMessage(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Notification from Wave Link
        if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var method = methodElement.GetString() ?? "";
            root.TryGetProperty("params", out var parameters);

            if (method == "outputDevicesChanged")
            {
                var state = ParseState(parameters);
                if (state is not null)
                {
                    Publish(state);
                    return;
                }
            }

            // Any other output-related notification (including ones newer Wave Link versions may add):
            // re-read the full state rather than guessing at partial payloads.
            if (method.Contains("output", StringComparison.OrdinalIgnoreCase))
            {
                QueueRefresh();
            }

            return;
        }

        // Response to one of our requests
        if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number &&
            idElement.TryGetInt32(out var id) && _pending.TryRemove(id, out var tcs))
        {
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                tcs.TrySetException(new InvalidOperationException(Json.Str(error, "message") ?? error.GetRawText()));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return; // one is already on its way
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150).ConfigureAwait(false); // coalesce bursts of notifications
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Log.Error("Wave Link refresh failed", ex);
            }
        });
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException("Wave Link disconnected."));
            }
        }
    }

    // ───────────────────────── state ─────────────────────────

    private WaveLinkState? ParseState(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasMain = Json.TryProp(e, "mainOutput", out var main) && main.ValueKind == JsonValueKind.Object;
        var hasDevices = Json.TryProp(e, "outputDevices", out var devices) && devices.ValueKind == JsonValueKind.Array;
        if (!hasMain && !hasDevices)
        {
            return null;
        }

        WaveLinkState? previous;
        lock (_stateGate)
        {
            previous = _latest;
        }

        var mainDeviceId = hasMain ? Json.Str(main, "outputDeviceId") : previous?.MainDeviceId;
        var mainOutputId = hasMain ? Json.Str(main, "outputId") ?? "" : previous?.MainOutputId ?? "";

        List<WaveLinkOutput> outputs;
        if (hasDevices)
        {
            outputs = [];
            foreach (var device in devices.EnumerateArray())
            {
                var deviceId = Json.Str(device, "id") ?? "";
                var deviceName = Json.Str(device, "name") ?? deviceId;
                var isWave = Json.TryProp(device, "isWaveDevice", out var wave) && wave.ValueKind == JsonValueKind.True;

                if (Json.TryProp(device, "outputs", out var deviceOutputs) &&
                    deviceOutputs.ValueKind == JsonValueKind.Array && deviceOutputs.GetArrayLength() > 0)
                {
                    foreach (var output in deviceOutputs.EnumerateArray())
                    {
                        outputs.Add(new WaveLinkOutput(deviceId, deviceName,
                            Json.Str(output, "id") ?? "", Json.Str(output, "name") ?? "", isWave));
                    }
                }
                else
                {
                    outputs.Add(new WaveLinkOutput(deviceId, deviceName, "", "", isWave));
                }
            }
        }
        else
        {
            outputs = previous?.Outputs.ToList() ?? [];
        }

        return new WaveLinkState(mainDeviceId, mainOutputId, outputs);
    }

    private void Publish(WaveLinkState state)
    {
        lock (_stateGate)
        {
            _latest = state;
            if (state.Signature == _signature)
            {
                return;
            }

            _signature = state.Signature;
        }

        Post(() =>
        {
            State = state;
            StateChanged?.Invoke(state);
        });
    }

    private void Post(Action action) => _ui.Post(_ => action(), null);

    public void Dispose()
    {
        _cts.Cancel();
        try { _ws?.Abort(); } catch { /* shutting down */ }
    }
}

/// <summary>Case-insensitive helpers, since this API isn't formally specified.</summary>
internal static class Json
{
    public static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in e.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static string? Str(JsonElement e, string name)
    {
        if (!TryProp(e, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}
