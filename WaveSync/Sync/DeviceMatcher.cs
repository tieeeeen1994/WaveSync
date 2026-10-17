using System.Text.RegularExpressions;
using WaveSync.Audio;
using WaveSync.WaveLink;

namespace WaveSync.Sync;

/// <summary>
/// Pairs Windows playback devices with Wave Link outputs. The two use different IDs, so pairing goes by
/// (in order of trust): manual overrides, Windows endpoint IDs embedded in Wave Link IDs, exact name
/// combinations, then word overlap. Each device and each output is used at most once.
/// </summary>
internal static partial class DeviceMatcher
{
    private const int MinimumScore = 300;

    public static Dictionary<string, WaveLinkOutput> Match(
        IReadOnlyList<AudioDevice> windowsDevices,
        IReadOnlyList<WaveLinkOutput> waveLinkOutputs,
        IReadOnlyList<DeviceOverride> overrides)
    {
        var candidates = new List<(int Score, AudioDevice Device, WaveLinkOutput Output)>();
        foreach (var device in windowsDevices)
        {
            foreach (var output in waveLinkOutputs)
            {
                var score = Score(device, output, overrides);
                if (score >= MinimumScore)
                {
                    candidates.Add((score, device, output));
                }
            }
        }

        var result = new Dictionary<string, WaveLinkOutput>(StringComparer.OrdinalIgnoreCase);
        var usedOutputs = new HashSet<string>();
        foreach (var (_, device, output) in candidates.OrderByDescending(c => c.Score))
        {
            if (!result.ContainsKey(device.Id) && usedOutputs.Add(output.Key))
            {
                result[device.Id] = output;
            }
        }

        return result;
    }

    public static bool OverrideMatchesWindows(DeviceOverride o, AudioDevice device) =>
        !string.IsNullOrWhiteSpace(o.Windows) &&
        (string.Equals(o.Windows, device.Id, StringComparison.OrdinalIgnoreCase) ||
         device.FriendlyName.Contains(o.Windows, StringComparison.OrdinalIgnoreCase));

    public static bool OverrideMatchesWaveLink(DeviceOverride o, WaveLinkOutput output) =>
        !string.IsNullOrWhiteSpace(o.WaveLink) &&
        (string.Equals(o.WaveLink, output.Key, StringComparison.OrdinalIgnoreCase) ||
         output.DisplayName.Contains(o.WaveLink, StringComparison.OrdinalIgnoreCase));

    private static int Score(AudioDevice device, WaveLinkOutput output, IReadOnlyList<DeviceOverride> overrides)
    {
        foreach (var o in overrides)
        {
            if (OverrideMatchesWindows(o, device) && OverrideMatchesWaveLink(o, output))
            {
                return 1000;
            }
        }

        // Wave Link may use the Windows endpoint ID (or its GUID) as its own device/output ID.
        if (string.Equals(output.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(output.OutputId, device.Id, StringComparison.OrdinalIgnoreCase))
        {
            return 950;
        }

        var guid = EndpointGuid(device.Id);
        if (guid is not null &&
            (output.DeviceId.Contains(guid, StringComparison.OrdinalIgnoreCase) ||
             output.OutputId.Contains(guid, StringComparison.OrdinalIgnoreCase)))
        {
            return 900;
        }

        var full = Normalize(device.FriendlyName);
        var endpoint = Normalize(device.EndpointName);
        var adapter = Normalize(device.AdapterName);
        var deviceName = Normalize(output.DeviceName);
        var outputName = Normalize(output.OutputName);

        var combos = new[] { deviceName, outputName, Join(outputName, deviceName), Join(deviceName, outputName) }
            .Where(c => c.Length > 0);
        if (full.Length > 0 && combos.Contains(full))
        {
            return 800;
        }

        if (endpoint.Length > 0 && adapter.Length > 0 &&
            ((deviceName == adapter && outputName == endpoint) || (deviceName == endpoint && outputName == adapter)))
        {
            return 780;
        }

        // Fuzzy: share of words in common.
        var windowsWords = Words(full);
        var waveLinkWords = Words(Join(deviceName, outputName));
        if (windowsWords.Count == 0 || waveLinkWords.Count == 0)
        {
            return 0;
        }

        var common = windowsWords.Intersect(waveLinkWords).Count();
        var union = windowsWords.Union(waveLinkWords).Count();
        var similarity = (double)common / union;
        return similarity >= 0.5 ? (int)(300 + 400 * similarity) : 0;
    }

    /// <summary>"{0.0.0.00000000}.{a1b2...}" → "{a1b2...}"</summary>
    private static string? EndpointGuid(string id)
    {
        var start = id.LastIndexOf('{');
        return start > 0 && id.EndsWith('}') ? id[start..] : null;
    }

    /// <summary>Lowercase, drop Windows' "2- " duplicate prefixes and punctuation.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return "";
        }

        s = s.ToLowerInvariant();
        s = DuplicatePrefix().Replace(s, " ");
        s = NonWord().Replace(s, " ");
        return s.Trim();
    }

    private static string Join(string a, string b) => (a + " " + b).Trim();

    private static HashSet<string> Words(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d+\s*-\s+")]
    private static partial Regex DuplicatePrefix();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
