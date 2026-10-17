namespace WaveSync.WaveLink;

/// <summary>One selectable playback target in Wave Link (an output on an output device).</summary>
internal sealed record WaveLinkOutput(string DeviceId, string DeviceName, string OutputId, string OutputName, bool IsWaveDevice)
{
    /// <summary>Stable identity used for comparisons and in config overrides.</summary>
    public string Key => $"{DeviceId}|{OutputId}";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(OutputName) || string.Equals(OutputName, DeviceName, StringComparison.OrdinalIgnoreCase)
            ? DeviceName
            : $"{OutputName} ({DeviceName})";
}

/// <summary>Snapshot of Wave Link's output devices and which one is the main (monitor) output.</summary>
internal sealed record WaveLinkState(string? MainDeviceId, string MainOutputId, IReadOnlyList<WaveLinkOutput> Outputs)
{
    public string? MainKey => MainDeviceId is null ? null : $"{MainDeviceId}|{MainOutputId}";

    public WaveLinkOutput? Main =>
        Outputs.FirstOrDefault(o => o.Key == MainKey)
        // Some devices report a main output without an output id; fall back to the device.
        ?? Outputs.FirstOrDefault(o => o.DeviceId == MainDeviceId);

    /// <summary>Changes only when the main output or the set of outputs changes (not on volume/mute changes).</summary>
    public string Signature => MainKey + "#" + string.Join(";", Outputs.Select(o => o.Key + "=" + o.DisplayName));
}
