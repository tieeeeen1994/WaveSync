using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveSync;

internal enum WindowsDefaultMode
{
    /// <summary>
    /// Change the Windows default only when it is currently a physical device.
    /// If Windows is routed through a Wave Link virtual device, leave it there so audio keeps going through the mixer.
    /// </summary>
    Auto,

    /// <summary>Always point the Windows default at the selected physical device (bypasses the Wave Link mixer).</summary>
    Always,

    /// <summary>Never touch the Windows default; only Wave Link's output changes.</summary>
    Never,
}

internal sealed class HotkeyBinding
{
    /// <summary>e.g. "Ctrl+Alt+F1", "Ctrl+Shift+1", "Win+Alt+H".</summary>
    public string Keys { get; set; } = "";

    /// <summary>Device to select: full name, part of the name, or a Windows endpoint ID.</summary>
    public string? Device { get; set; }

    /// <summary>"cycle" to step through devices instead of selecting one.</summary>
    public string? Action { get; set; }
}

internal sealed class DeviceOverride
{
    /// <summary>Windows device name (or part of it), or its endpoint ID.</summary>
    public string Windows { get; set; } = "";

    /// <summary>Wave Link output name (or part of it), or its "deviceId|outputId" key from the diagnostics file.</summary>
    public string WaveLink { get; set; } = "";
}

internal sealed class AppConfig
{
    public WindowsDefaultMode WindowsDefaultMode { get; set; } = WindowsDefaultMode.Auto;
    public bool SetCommunicationsDevice { get; set; } = true;
    public bool MirrorWindowsToWaveLink { get; set; } = true;
    public bool MirrorWaveLinkToWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    /// <summary>0 = auto-discover (ws-info.json, then ports 1884–1893).</summary>
    public int WaveLinkPort { get; set; }

    /// <summary>How often to re-read Wave Link's outputs as a safety net. 0 disables polling.</summary>
    public int WaveLinkPollMs { get; set; } = 3000;

    /// <summary>Windows devices whose names contain any of these are treated as Wave Link's own virtual devices.</summary>
    public List<string> VirtualDeviceNamePatterns { get; set; } = ["Wave Link", "Elgato Virtual"];

    /// <summary>Devices (name or part of name) to hide from the menu and from cycling.</summary>
    public List<string> HiddenDevices { get; set; } = [];

    /// <summary>Devices to step through with the "cycle" hotkey, in order. Empty = every visible device.</summary>
    public List<string> CycleDevices { get; set; } = [];

    public List<HotkeyBinding> Hotkeys { get; set; } = [new HotkeyBinding { Keys = "Ctrl+Alt+F12", Action = "cycle" }];

    /// <summary>Manual pairings for devices whose names don't match automatically.</summary>
    public List<DeviceOverride> DeviceOverrides { get; set; } = [];

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveSync");

    public static string FilePath => Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions);
                if (config is not null)
                {
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read config.json, using defaults", ex);
            return new AppConfig();
        }

        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't save config.json", ex);
        }
    }
}
