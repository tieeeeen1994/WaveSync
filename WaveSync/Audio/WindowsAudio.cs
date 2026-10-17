using System.Runtime.InteropServices;

namespace WaveSync.Audio;

/// <param name="Id">Windows endpoint ID, e.g. {0.0.0.00000000}.{guid}</param>
/// <param name="FriendlyName">What Sound settings shows, e.g. "Headphones (Elgato Wave:3)"</param>
/// <param name="EndpointName">The part before the brackets, e.g. "Headphones"</param>
/// <param name="AdapterName">The part in brackets, e.g. "Elgato Wave:3"</param>
internal sealed record AudioDevice(string Id, string FriendlyName, string EndpointName, string AdapterName);

/// <summary>
/// Windows playback devices: list them, read and set the default, and get told when anything changes.
/// Create and use on the UI thread; change notifications are posted back to it.
/// </summary>
internal sealed class WindowsAudio : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;
    private bool _disposed;

    /// <summary>Raised (on the UI thread) when the default device changes or devices are added/removed/enabled/disabled.</summary>
    public event Action? Changed;

    public WindowsAudio(SynchronizationContext ui)
    {
        _ui = ui;
        _enumerator = (IMMDeviceEnumerator)CreateComObject(CoreAudioIds.MMDeviceEnumeratorClsid);
        _client = new NotificationClient(this);
        var hr = _enumerator.RegisterEndpointNotificationCallback(_client);
        if (hr != 0)
        {
            Log.Error($"RegisterEndpointNotificationCallback failed (0x{hr:X8}); Windows changes won't be detected");
        }
    }

    public List<AudioDevice> GetPlaybackDevices()
    {
        var devices = new List<AudioDevice>();
        if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, CoreAudioIds.DeviceStateActive, out var collection) != 0 || collection is null)
        {
            return devices;
        }

        try
        {
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device is null)
                {
                    continue;
                }

                try
                {
                    var described = Describe(device);
                    if (described is not null)
                    {
                        devices.Add(described);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return devices.OrderBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string? GetDefaultPlaybackDeviceId()
    {
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device) != 0 || device is null)
        {
            return null; // E_NOTFOUND when there are no playback devices at all
        }

        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    public void SetDefaultPlaybackDevice(string deviceId, bool includeCommunications)
    {
        var policy = (IPolicyConfig)CreateComObject(CoreAudioIds.PolicyConfigClientClsid);
        try
        {
            ThrowIfFailed(policy.SetDefaultEndpoint(deviceId, ERole.Console), "SetDefaultEndpoint(Console)");
            ThrowIfFailed(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia), "SetDefaultEndpoint(Multimedia)");
            if (includeCommunications)
            {
                ThrowIfFailed(policy.SetDefaultEndpoint(deviceId, ERole.Communications), "SetDefaultEndpoint(Communications)");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    private static AudioDevice? Describe(IMMDevice device)
    {
        if (device.GetId(out var id) != 0 || string.IsNullOrEmpty(id))
        {
            return null;
        }

        string friendly = "", endpoint = "", adapter = "";
        if (device.OpenPropertyStore(CoreAudioIds.StgmRead, out var store) == 0 && store is not null)
        {
            try
            {
                friendly = ReadString(store, CoreAudioIds.DeviceFriendlyName);
                endpoint = ReadString(store, CoreAudioIds.DeviceDescription);
                adapter = ReadString(store, CoreAudioIds.InterfaceFriendlyName);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        if (friendly.Length == 0)
        {
            friendly = endpoint.Length > 0 ? endpoint : id;
        }

        return new AudioDevice(id, friendly, endpoint, adapter);
    }

    private static string ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) != 0)
        {
            return "";
        }

        try
        {
            return value.VarType == CoreAudioIds.VtLpwstr && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.Pointer) ?? ""
                : "";
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    private static object CreateComObject(Guid clsid)
    {
        var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException($"Couldn't create COM object {clsid}");
    }

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr != 0)
        {
            throw new COMException($"{what} failed", hr);
        }
    }

    private void RaiseChanged() => _ui.Post(_ =>
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }, null);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch
        {
            // ignored on shutdown
        }

        Marshal.ReleaseComObject(_enumerator);
    }

    /// <summary>
    /// Called by Windows on a background thread. Must return quickly and must not call back into Core Audio,
    /// so it only posts a "something changed" signal to the UI thread.
    /// </summary>
    [ComVisible(true)]
    public sealed class NotificationClient : IMMNotificationClient
    {
        private readonly WindowsAudio _owner;

        internal NotificationClient(WindowsAudio owner) => _owner = owner;

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            _owner.RaiseChanged();
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            _owner.RaiseChanged();
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            _owner.RaiseChanged();
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Console)
            {
                _owner.RaiseChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
