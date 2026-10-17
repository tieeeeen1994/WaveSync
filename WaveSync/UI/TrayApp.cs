using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using WaveSync.Audio;
using WaveSync.Sync;
using WaveSync.WaveLink;

namespace WaveSync.UI;

/// <summary>The tray icon, its menu, notifications and hotkeys. Owns everything else.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _icon;
    private readonly IntPtr _iconHandle;
    private readonly WindowsAudio _windows;
    private readonly WaveLinkClient _waveLink;
    private readonly SyncController _sync;
    private readonly HotkeyManager _hotkeys = new();
    private AppConfig _config;

    public TrayApp()
    {
        _config = AppConfig.Load();
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _windows = new WindowsAudio(ui);
        _waveLink = new WaveLinkClient(ui, () => _config);
        _sync = new SyncController(_windows, _waveLink, _config);

        (var icon, _iconHandle) = CreateIcon();
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "WaveSync",
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // NotifyIcon only opens its menu on right-click; reuse the internal method for left-click too.
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_icon, null);
            }
        };

        _sync.Changed += UpdateTooltip;
        _sync.Notice += Notify;

        RegisterHotkeys();
        _sync.Start();
    }

    // ───────────────────────── menu ─────────────────────────

    private void BuildMenu()
    {
        _sync.RefreshWindows();
        var current = _sync.Current;
        var windowsDefault = _sync.WindowsDefault;

        _menu.SuspendLayout();
        DisposeItems(_menu.Items);

        _menu.Items.Add(Info($"Output: {current?.Name ?? "none selected"}", bold: true));
        _menu.Items.Add(Info(_waveLink.IsConnected
            ? $"Wave Link: connected (port {_waveLink.Port})"
            : "Wave Link: not running"));
        _menu.Items.Add(Info($"Windows default: {windowsDefault?.FriendlyName ?? "none"}" +
                             (_sync.WindowsRoutesThroughWaveLink ? " (through Wave Link)" : "")));
        _menu.Items.Add(new ToolStripSeparator());

        // The selector itself
        var physical = _sync.PhysicalDevices;
        foreach (var device in physical)
        {
            var label = device.FriendlyName;
            if (_waveLink.IsConnected && _sync.PairFor(device) is null)
            {
                label += "   (Windows only)";
            }

            var item = new ToolStripMenuItem(label) { Checked = current?.WindowsId == device.Id };
            item.Click += (_, _) => _sync.Select(device);
            _menu.Items.Add(item);
        }

        foreach (var output in _sync.WaveLinkOnlyOutputs)
        {
            var item = new ToolStripMenuItem($"{output.DisplayName}   (Wave Link only)")
            {
                Checked = current?.WindowsId is null && current?.WaveLinkKey == output.Key,
            };
            item.Click += (_, _) => _sync.Select(output);
            _menu.Items.Add(item);
        }

        if (physical.Count == 0 && _sync.WaveLinkOnlyOutputs.Count == 0)
        {
            _menu.Items.Add(Info("No playback devices found"));
        }

        _menu.Items.Add(new ToolStripSeparator());

        // Routing Windows through Wave Link
        var virtualDevices = _sync.VirtualDevices;
        if (virtualDevices.Count > 0)
        {
            var routing = new ToolStripMenuItem("Send Windows audio through Wave Link");
            foreach (var device in virtualDevices)
            {
                var item = new ToolStripMenuItem(device.FriendlyName) { Checked = windowsDefault?.Id == device.Id };
                item.Click += (_, _) => _sync.RouteWindowsThrough(device);
                routing.DropDownItems.Add(item);
            }

            _menu.Items.Add(routing);
        }

        // Settings
        var settings = new ToolStripMenuItem("Settings");
        var mode = new ToolStripMenuItem("Change Windows default");
        mode.DropDownItems.Add(ModeItem(WindowsDefaultMode.Auto, "Auto (keep it on Wave Link if it's routed there)"));
        mode.DropDownItems.Add(ModeItem(WindowsDefaultMode.Always, "Always (bypasses the Wave Link mixer)"));
        mode.DropDownItems.Add(ModeItem(WindowsDefaultMode.Never, "Never (only change Wave Link)"));
        settings.DropDownItems.Add(mode);
        settings.DropDownItems.Add(Toggle("Follow changes made in Windows", _config.MirrorWindowsToWaveLink,
            v => _config.MirrorWindowsToWaveLink = v));
        settings.DropDownItems.Add(Toggle("Follow changes made in Wave Link", _config.MirrorWaveLinkToWindows,
            v => _config.MirrorWaveLinkToWindows = v));
        settings.DropDownItems.Add(Toggle("Also set the communications device", _config.SetCommunicationsDevice,
            v => _config.SetCommunicationsDevice = v));
        settings.DropDownItems.Add(Toggle("Show notifications", _config.ShowNotifications,
            v => _config.ShowNotifications = v));
        settings.DropDownItems.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupRegistration.IsEnabled };
        startup.Click += (_, _) => StartupRegistration.Set(!startup.Checked);
        settings.DropDownItems.Add(startup);
        settings.DropDownItems.Add(new ToolStripSeparator());

        settings.DropDownItems.Add(Command("Open settings file", () =>
        {
            _config.Save();
            OpenFile(AppConfig.FilePath);
        }));
        settings.DropDownItems.Add(Command("Reload settings file", ReloadConfig));
        settings.DropDownItems.Add(Command("Show device diagnostics", ShowDiagnostics));
        settings.DropDownItems.Add(Command("Open log", () => OpenFile(Log.FilePath)));
        _menu.Items.Add(settings);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Command("Exit", ExitApp));

        _menu.ResumeLayout();
    }

    private static ToolStripMenuItem Info(string text, bool bold = false)
    {
        var item = new ToolStripMenuItem(text) { Enabled = false };
        if (bold)
        {
            item.Font = new Font(item.Font, FontStyle.Bold);
        }

        return item;
    }

    private static ToolStripMenuItem Command(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    private ToolStripMenuItem Toggle(string text, bool value, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = value };
        item.Click += (_, _) =>
        {
            set(!value);
            SaveAndApplyConfig();
        };
        return item;
    }

    private ToolStripMenuItem ModeItem(WindowsDefaultMode mode, string text)
    {
        var item = new ToolStripMenuItem(text) { Checked = _config.WindowsDefaultMode == mode };
        item.Click += (_, _) =>
        {
            _config.WindowsDefaultMode = mode;
            SaveAndApplyConfig();
        };
        return item;
    }

    private static void DisposeItems(ToolStripItemCollection items)
    {
        var old = items.Cast<ToolStripItem>().ToList();
        items.Clear();
        foreach (var item in old)
        {
            item.Dispose();
        }
    }

    // ───────────────────────── actions ─────────────────────────

    private void SaveAndApplyConfig()
    {
        _config.Save();
        _sync.UpdateConfig(_config);
    }

    private void ReloadConfig()
    {
        _config = AppConfig.Load();
        _sync.UpdateConfig(_config);
        var problems = RegisterHotkeys();
        Notify(problems.Count == 0 ? "Settings reloaded." : "Settings reloaded. " + string.Join(" ", problems));
    }

    private List<string> RegisterHotkeys()
    {
        _hotkeys.Clear();
        var problems = new List<string>();

        foreach (var binding in _config.Hotkeys)
        {
            if (string.IsNullOrWhiteSpace(binding.Keys))
            {
                continue;
            }

            Action? action = null;
            if (string.Equals(binding.Action, "cycle", StringComparison.OrdinalIgnoreCase))
            {
                action = () => Notify(_sync.Cycle(), force: true);
            }
            else if (!string.IsNullOrWhiteSpace(binding.Device))
            {
                var name = binding.Device;
                action = () => Notify(_sync.SelectByName(name), force: true);
            }

            if (action is null)
            {
                problems.Add($"Hotkey {binding.Keys} needs a \"device\" or \"action\": \"cycle\".");
                continue;
            }

            var error = _hotkeys.Register(binding.Keys, action);
            if (error is not null)
            {
                problems.Add($"Hotkey skipped: {error}.");
            }
        }

        foreach (var problem in problems)
        {
            Log.Error(problem);
        }

        if (problems.Count > 0 && _icon is not null)
        {
            Notify(string.Join(" ", problems));
        }

        return problems;
    }

    private void ShowDiagnostics()
    {
        try
        {
            _sync.RefreshWindows();
            var path = Path.Combine(AppConfig.Folder, "diagnostics.txt");
            Directory.CreateDirectory(AppConfig.Folder);
            File.WriteAllText(path, _sync.BuildDiagnostics());
            OpenFile(path);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't write diagnostics", ex);
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "");
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't open {path}", ex);
        }
    }

    /// <param name="force">Hotkey feedback; shown even when notifications are off, since there's no other cue.</param>
    private void Notify(string message) => Notify(message, force: false);

    private void Notify(string message, bool force)
    {
        if (_config.ShowNotifications || force)
        {
            _icon.ShowBalloonTip(2500, "WaveSync", message, ToolTipIcon.None);
        }
    }

    private void UpdateTooltip()
    {
        var text = $"WaveSync: {_sync.Current?.Name ?? "no output selected"}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        _hotkeys.Dispose();
        _waveLink.Dispose();
        _sync.Dispose();
        _windows.Dispose();
        _icon.Dispose();
        NativeMethods.DestroyIcon(_iconHandle);
        ExitThread();
    }

    /// <summary>A small speaker glyph, drawn at runtime so the project has no binary assets.</summary>
    private static (Icon Icon, IntPtr Handle) CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var color = Color.FromArgb(0x4C, 0xB4, 0xFF);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            g.FillRectangle(brush, 3, 11, 7, 10);
            g.FillPolygon(brush, new[] { new Point(9, 11), new Point(17, 4), new Point(17, 28), new Point(9, 21) });
            g.DrawArc(pen, 13, 10, 8, 12, -55, 110);
            g.DrawArc(pen, 13, 5, 15, 22, -55, 110);
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }
}
