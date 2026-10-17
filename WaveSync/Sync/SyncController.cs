using System.Text;
using WaveSync.Audio;
using WaveSync.WaveLink;

namespace WaveSync.Sync;

/// <summary>What the user currently hears. Either side may be missing (a device only one of them knows about).</summary>
internal sealed record Selection(string Name, string? WindowsId, string? WaveLinkKey);

/// <summary>
/// The single source of truth. Every change, from the tray menu, a hotkey, Windows or Wave Link, goes through
/// here, updates <see cref="Current"/>, and is pushed to whichever side doesn't have it yet.
///
/// Loop prevention:
///   • Windows: after a debounce, the real default is re-read; if it equals Current, it's our own echo.
///   • Wave Link: writes are async, so for a short window after we set it, states that still show the old
///     output are ignored as stale instead of being treated as a user change.
///
/// Everything here runs on the UI thread.
/// </summary>
internal sealed class SyncController : IDisposable
{
    private static readonly TimeSpan WaveLinkWriteGuard = TimeSpan.FromSeconds(2.5);

    private readonly WindowsAudio _windows;
    private readonly WaveLinkClient _waveLink;
    private readonly System.Windows.Forms.Timer _windowsDebounce = new() { Interval = 250 };

    private AppConfig _config;
    private Dictionary<string, WaveLinkOutput> _pairs = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingWaveLinkKey;
    private DateTime _pendingWaveLinkUntil;
    private string? _lastWaveLinkMainKey;
    private bool _reconcileOnNextWaveLinkState;

    public SyncController(WindowsAudio windows, WaveLinkClient waveLink, AppConfig config)
    {
        _windows = windows;
        _waveLink = waveLink;
        _config = config;

        _windowsDebounce.Tick += (_, _) =>
        {
            _windowsDebounce.Stop();
            OnWindowsSettled();
        };
        _windows.Changed += () =>
        {
            _windowsDebounce.Stop();
            _windowsDebounce.Start();
        };
        _waveLink.ConnectionChanged += OnWaveLinkConnectionChanged;
        _waveLink.StateChanged += OnWaveLinkState;
    }

    public Selection? Current { get; private set; }
    public IReadOnlyList<AudioDevice> WindowsDevices { get; private set; } = [];
    public string? WindowsDefaultId { get; private set; }

    /// <summary>Raised whenever anything the menu or tooltip shows may have changed.</summary>
    public event Action? Changed;

    /// <summary>Short user-facing messages (shown as tray notifications).</summary>
    public event Action<string>? Notice;

    // ───────────────────────── views for the UI ─────────────────────────

    public AudioDevice? WindowsDefault => WindowsDevices.FirstOrDefault(d => SameId(d.Id, WindowsDefaultId));

    public bool WindowsRoutesThroughWaveLink => WindowsDefault is { } d && IsVirtual(d);

    public IReadOnlyList<AudioDevice> PhysicalDevices =>
        WindowsDevices.Where(d => !IsVirtual(d) && !IsHidden(d.FriendlyName)).ToList();

    public IReadOnlyList<AudioDevice> VirtualDevices => WindowsDevices.Where(IsVirtual).ToList();

    public IReadOnlyList<WaveLinkOutput> WaveLinkOutputs =>
        (_waveLink.State?.Outputs ?? [])
        .Where(o => !IsVirtualName(o.DeviceName) && !IsVirtualName(o.OutputName) && !IsHidden(o.DisplayName))
        .ToList();

    /// <summary>Wave Link outputs that no Windows device was paired with.</summary>
    public IReadOnlyList<WaveLinkOutput> WaveLinkOnlyOutputs =>
        WaveLinkOutputs.Where(o => !_pairs.Values.Any(p => p.Key == o.Key)).ToList();

    public WaveLinkOutput? PairFor(AudioDevice device) => _pairs.GetValueOrDefault(device.Id);

    // ───────────────────────── lifecycle ─────────────────────────

    public void Start()
    {
        RefreshWindows();
        if (WindowsDefault is { } def && !IsVirtual(def))
        {
            Current = SelectionFor(def);
        }

        Log.Info($"Windows default at startup: {WindowsDefault?.FriendlyName ?? "(none)"}");
        _waveLink.Start();
        Changed?.Invoke();
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        RebuildPairs();
        Changed?.Invoke();
    }

    public void RefreshWindows()
    {
        try
        {
            WindowsDevices = _windows.GetPlaybackDevices();
            WindowsDefaultId = _windows.GetDefaultPlaybackDeviceId();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read Windows playback devices", ex);
        }

        RebuildPairs();
    }

    // ───────────────────────── user actions ─────────────────────────

    /// <summary>Make this device the output everywhere (tray click or hotkey).</summary>
    public void Select(AudioDevice device)
    {
        Log.Info($"Selected {device.FriendlyName}");
        Current = SelectionFor(device);
        var pair = PairFor(device);
        var waveLinkCanTakeIt = _waveLink.IsConnected && pair is not null;

        PushToWaveLink(device.FriendlyName, pair);

        if (ShouldSetWindows(waveLinkCanTakeIt))
        {
            PushToWindows(device);
        }
        else if (!waveLinkCanTakeIt)
        {
            Notice?.Invoke($"{device.FriendlyName}: Wave Link can't use it and \"Change Windows default\" is set to Never.");
        }

        Changed?.Invoke();
    }

    /// <summary>Select an output only Wave Link knows about.</summary>
    public void Select(WaveLinkOutput output)
    {
        Log.Info($"Selected Wave Link output {output.DisplayName}");
        Current = new Selection(output.DisplayName, null, output.Key);
        PushToWaveLink(output.DisplayName, output);
        Changed?.Invoke();
    }

    /// <summary>Point Windows at a Wave Link virtual device (sends system audio through the mixer). Wave Link is unchanged.</summary>
    public void RouteWindowsThrough(AudioDevice virtualDevice)
    {
        PushToWindows(virtualDevice);
        Changed?.Invoke();
    }

    /// <returns>A message for the user.</returns>
    public string SelectByName(string name)
    {
        var physical = PhysicalDevices;
        var device = physical.FirstOrDefault(d => d.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase))
                     ?? physical.FirstOrDefault(d => SameId(d.Id, name))
                     ?? physical.FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (device is not null)
        {
            Select(device);
            return $"Output: {device.FriendlyName}";
        }

        var output = WaveLinkOnlyOutputs.FirstOrDefault(o => o.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (output is not null)
        {
            Select(output);
            return $"Output: {output.DisplayName}";
        }

        return $"No playback device matches \"{name}\".";
    }

    /// <returns>A message for the user.</returns>
    public string Cycle()
    {
        var physical = PhysicalDevices;
        var list = _config.CycleDevices.Count == 0
            ? physical.ToList()
            : _config.CycleDevices
                .Select(n => physical.FirstOrDefault(d => d.FriendlyName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .OfType<AudioDevice>()
                .Distinct()
                .ToList();

        if (list.Count == 0)
        {
            return "No devices to cycle through.";
        }

        var index = list.FindIndex(d => SameId(d.Id, Current?.WindowsId));
        var next = list[(index + 1) % list.Count];
        Select(next);
        return $"Output: {next.FriendlyName}";
    }

    // ───────────────────────── incoming changes ─────────────────────────

    private void OnWindowsSettled()
    {
        RefreshWindows(); // re-read the real default rather than trusting event order

        var def = WindowsDefault;
        if (def is not null && !IsVirtual(def) && !SameId(Current?.WindowsId, def.Id))
        {
            Log.Info($"Windows default changed to {def.FriendlyName}");
            Current = SelectionFor(def);
            if (_config.MirrorWindowsToWaveLink)
            {
                PushToWaveLink(def.FriendlyName, PairFor(def));
            }
        }

        // A virtual default just means Windows now goes through the mixer; nothing to mirror.
        Changed?.Invoke();
    }

    private void OnWaveLinkConnectionChanged()
    {
        if (_waveLink.IsConnected)
        {
            Log.Info("Wave Link connected");
            _reconcileOnNextWaveLinkState = true;
        }
        else
        {
            Log.Info("Wave Link disconnected");
            _pendingWaveLinkKey = null;
            _lastWaveLinkMainKey = null;
            RebuildPairs();
        }

        Changed?.Invoke();
    }

    private void OnWaveLinkState(WaveLinkState state)
    {
        RebuildPairs();
        var key = state.MainKey;

        if (_pendingWaveLinkKey is not null)
        {
            if (key == _pendingWaveLinkKey || DateTime.UtcNow > _pendingWaveLinkUntil)
            {
                _pendingWaveLinkKey = null;
            }
            else
            {
                Changed?.Invoke(); // stale: Wave Link hasn't applied our change yet
                return;
            }
        }

        var mainChanged = key != _lastWaveLinkMainKey;
        _lastWaveLinkMainKey = key;

        if (_reconcileOnNextWaveLinkState)
        {
            _reconcileOnNextWaveLinkState = false;

            // On (re)connect: if Windows plays straight to a physical device, that's what you hear, so Wave Link follows it.
            // If Windows goes through Wave Link, Wave Link's own output is the truth, handled below.
            if (WindowsDefault is { } def && !IsVirtual(def) && _config.MirrorWindowsToWaveLink)
            {
                Current = SelectionFor(def);
                PushToWaveLink(def.FriendlyName, PairFor(def));
                Changed?.Invoke();
                return;
            }

            mainChanged = true;
        }

        if (!mainChanged || key is null || Current?.WaveLinkKey == key)
        {
            Changed?.Invoke();
            return;
        }

        var windowsDevice = WindowsDeviceFor(key);
        var main = state.Main;
        Current = new Selection(windowsDevice?.FriendlyName ?? main?.DisplayName ?? "Wave Link output", windowsDevice?.Id, key);
        Log.Info($"Wave Link output changed to {main?.DisplayName ?? key}");

        if (_config.MirrorWaveLinkToWindows && windowsDevice is not null && ShouldSetWindows(waveLinkCanTakeIt: true))
        {
            PushToWindows(windowsDevice);
        }

        Changed?.Invoke();
    }

    // ───────────────────────── pushing changes out ─────────────────────────

    private bool ShouldSetWindows(bool waveLinkCanTakeIt) => _config.WindowsDefaultMode switch
    {
        WindowsDefaultMode.Always => true,
        WindowsDefaultMode.Never => false,
        // Auto: keep Windows on the Wave Link virtual device if it's there and Wave Link can play the selection.
        _ => !WindowsRoutesThroughWaveLink || !waveLinkCanTakeIt,
    };

    private void PushToWaveLink(string name, WaveLinkOutput? output)
    {
        var state = _waveLink.State;
        if (!_waveLink.IsConnected || state is null)
        {
            return;
        }

        if (output is null)
        {
            Notice?.Invoke($"Wave Link has no output matching \"{name}\". You can pair them in the settings file (deviceOverrides).");
            return;
        }

        if (state.MainKey == output.Key)
        {
            return;
        }

        _pendingWaveLinkKey = output.Key;
        _pendingWaveLinkUntil = DateTime.UtcNow + WaveLinkWriteGuard;
        _ = SetWaveLinkAsync(output);
    }

    private async Task SetWaveLinkAsync(WaveLinkOutput output)
    {
        try
        {
            await _waveLink.SetMainOutputAsync(output);
            Log.Info($"Wave Link output set to {output.DisplayName}");
        }
        catch (Exception ex)
        {
            _pendingWaveLinkKey = null;
            Log.Error($"Couldn't set Wave Link output to {output.DisplayName}", ex);
            Notice?.Invoke($"Wave Link didn't accept the change: {ex.Message}");
        }
    }

    private void PushToWindows(AudioDevice device)
    {
        if (SameId(WindowsDefaultId, device.Id))
        {
            return;
        }

        try
        {
            _windows.SetDefaultPlaybackDevice(device.Id, _config.SetCommunicationsDevice);
            WindowsDefaultId = device.Id;
            Log.Info($"Windows default set to {device.FriendlyName}");
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't set Windows default to {device.FriendlyName}", ex);
            Notice?.Invoke($"Couldn't change the Windows default: {ex.Message}");
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    private void RebuildPairs()
    {
        var physical = WindowsDevices.Where(d => !IsVirtual(d)).ToList();
        _pairs = _waveLink.IsConnected
            ? DeviceMatcher.Match(physical, WaveLinkOutputs, _config.DeviceOverrides)
            : new Dictionary<string, WaveLinkOutput>(StringComparer.OrdinalIgnoreCase);
    }

    private Selection SelectionFor(AudioDevice device) =>
        new(device.FriendlyName, device.Id, PairFor(device)?.Key);

    private AudioDevice? WindowsDeviceFor(string waveLinkKey)
    {
        var id = _pairs.FirstOrDefault(p => p.Value.Key == waveLinkKey).Key;
        return id is null ? null : WindowsDevices.FirstOrDefault(d => SameId(d.Id, id));
    }

    public bool IsVirtual(AudioDevice device) => IsVirtualName(device.FriendlyName);

    private bool IsVirtualName(string name) =>
        _config.VirtualDeviceNamePatterns.Any(p => !string.IsNullOrWhiteSpace(p) && name.Contains(p, StringComparison.OrdinalIgnoreCase));

    private bool IsHidden(string name) =>
        _config.HiddenDevices.Any(h => !string.IsNullOrWhiteSpace(h) && name.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static bool SameId(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"WaveSync diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Current selection: {Current?.Name ?? "(none)"}");
        sb.AppendLine($"Change Windows default: {_config.WindowsDefaultMode}");
        sb.AppendLine();

        sb.AppendLine("== Windows playback devices (* = default) ==");
        foreach (var d in WindowsDevices)
        {
            sb.AppendLine($"{(SameId(d.Id, WindowsDefaultId) ? "*" : " ")} {d.FriendlyName}   [{(IsVirtual(d) ? "Wave Link virtual" : "physical")}]");
            sb.AppendLine($"    endpoint: \"{d.EndpointName}\"   adapter: \"{d.AdapterName}\"");
            sb.AppendLine($"    id: {d.Id}");
        }

        sb.AppendLine();
        sb.AppendLine(_waveLink.IsConnected
            ? $"== Wave Link outputs (port {_waveLink.Port}, * = main output) =="
            : "== Wave Link outputs: Wave Link not connected ==");
        foreach (var o in _waveLink.State?.Outputs ?? [])
        {
            sb.AppendLine($"{(o.Key == _waveLink.State?.MainKey ? "*" : " ")} {o.DisplayName}{(o.IsWaveDevice ? "   [Elgato device]" : "")}");
            sb.AppendLine($"    key: {o.Key}");
        }

        sb.AppendLine();
        sb.AppendLine("== Pairing ==");
        foreach (var d in WindowsDevices.Where(d => !IsVirtual(d)))
        {
            sb.AppendLine($"  {d.FriendlyName}  →  {PairFor(d)?.DisplayName ?? "(no Wave Link match)"}");
        }

        foreach (var o in WaveLinkOnlyOutputs)
        {
            sb.AppendLine($"  (no Windows match)  →  {o.DisplayName}");
        }

        sb.AppendLine();
        sb.AppendLine("To pair devices manually, add to deviceOverrides in config.json, e.g.:");
        sb.AppendLine("  \"deviceOverrides\": [ { \"windows\": \"Headphones (Elgato Wave:3)\", \"waveLink\": \"<key or name from above>\" } ]");
        return sb.ToString();
    }

    public void Dispose() => _windowsDebounce.Dispose();
}
