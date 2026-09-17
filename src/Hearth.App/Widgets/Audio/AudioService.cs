using System.Runtime.InteropServices;
using Hearth.Core.Diagnostics;
using Hearth.Core.Interop;

namespace Hearth.App.Widgets.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, bool IsInput);

/// <summary>
/// CoreAudio management service: enumerates audio endpoints (playback and recording),
/// observes volume and mute changes in real time, and switches default devices.
/// </summary>
public sealed class AudioService : IDisposable
{
    private static readonly Lazy<AudioService> Instance = new(() => new AudioService());
    public static AudioService Current => Instance.Value;

    private readonly object _lock = new();
    private IMMDeviceEnumerator? _enumerator;
    private NotificationClient? _notificationClient;
    private VolumeCallback? _renderVolumeCallback;
    private VolumeCallback? _captureVolumeCallback;
    private IAudioEndpointVolume? _renderVolume;
    private IAudioEndpointVolume? _captureVolume;

    public event Action? DevicesChanged;
    public event Action<bool, float, bool>? VolumeChanged; // (isInput, level, isMuted)

    public AudioService()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            _notificationClient = new NotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            RefreshEndpointVolumes();
        }
        catch (Exception ex)
        {
            Log.Error("AudioService initialization failed", ex);
        }
    }

    private void RefreshEndpointVolumes()
    {
        lock (_lock)
        {
            // Unregister old callbacks
            if (_renderVolume is not null && _renderVolumeCallback is not null)
            {
                try { _renderVolume.UnregisterControlChangeNotify(_renderVolumeCallback); } catch { }
                _renderVolume = null;
            }
            if (_captureVolume is not null && _captureVolumeCallback is not null)
            {
                try { _captureVolume.UnregisterControlChangeNotify(_captureVolumeCallback); } catch { }
                _captureVolume = null;
            }

            if (_enumerator is null) return;

            // Output (render) endpoint volume
            try
            {
                if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var renderDevice) == 0 && renderDevice is not null)
                {
                    var iid = AudioConstants.IID_IAudioEndpointVolume;
                    if (renderDevice.Activate(ref iid, AudioConstants.CLSCTX_ALL, IntPtr.Zero, out var iface) == 0 && iface is IAudioEndpointVolume vol)
                    {
                        _renderVolume = vol;
                        _renderVolumeCallback = new VolumeCallback(this, isInput: false);
                        _renderVolume.RegisterControlChangeNotify(_renderVolumeCallback);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Failed to activate render volume: {ex.Message}");
            }

            // Input (capture) endpoint volume
            try
            {
                if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out var captureDevice) == 0 && captureDevice is not null)
                {
                    var iid = AudioConstants.IID_IAudioEndpointVolume;
                    if (captureDevice.Activate(ref iid, AudioConstants.CLSCTX_ALL, IntPtr.Zero, out var iface) == 0 && iface is IAudioEndpointVolume vol)
                    {
                        _captureVolume = vol;
                        _captureVolumeCallback = new VolumeCallback(this, isInput: true);
                        _captureVolume.RegisterControlChangeNotify(_captureVolumeCallback);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Failed to activate capture volume: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices(bool input)
    {
        var list = new List<AudioDeviceInfo>();
        if (_enumerator is null) return list;

        lock (_lock)
        {
            var flow = input ? EDataFlow.eCapture : EDataFlow.eRender;
            string? defaultId = null;

            if (_enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var defDevice) == 0 && defDevice is not null)
            {
                if (defDevice.GetId(out var idStr) == 0) defaultId = idStr;
            }

            if (_enumerator.EnumAudioEndpoints(flow, AudioConstants.DEVICE_STATE_ACTIVE, out var collection) == 0 && collection is not null)
            {
                if (collection.GetCount(out var count) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (collection.Item(i, out var dev) == 0 && dev is not null)
                        {
                            if (dev.GetId(out var id) == 0)
                            {
                                var name = GetDeviceFriendlyName(dev) ?? (input ? "Microphone" : "Speaker");
                                var isDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                                list.Add(new AudioDeviceInfo(id, name, isDefault, input));
                            }
                        }
                    }
                }
            }
        }

        return list;
    }

    public AudioDeviceInfo? GetDefaultDevice(bool input)
    {
        if (_enumerator is null) return null;
        lock (_lock)
        {
            var flow = input ? EDataFlow.eCapture : EDataFlow.eRender;
            if (_enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var defDevice) == 0 && defDevice is not null)
            {
                if (defDevice.GetId(out var id) == 0)
                {
                    var name = GetDeviceFriendlyName(defDevice) ?? (input ? "Default Microphone" : "Default Speaker");
                    return new AudioDeviceInfo(id, name, true, input);
                }
            }
        }
        return null;
    }

    private static string? GetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(AudioConstants.STGM_READ, out var store) == 0 && store is not null)
            {
                var key = AudioConstants.PKEY_Device_FriendlyName;
                store.GetValue(ref key, out var pv);
                return pv.AsString();
            }
        }
        catch { }
        return null;
    }

    public float GetVolume(bool input)
    {
        lock (_lock)
        {
            var vol = input ? _captureVolume : _renderVolume;
            if (vol is not null)
            {
                try
                {
                    if (vol.GetMasterVolumeLevelScalar(out var level) == 0)
                        return level;
                }
                catch { }
            }
        }
        return 0f;
    }

    public void SetVolume(bool input, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        lock (_lock)
        {
            var vol = input ? _captureVolume : _renderVolume;
            if (vol is not null)
            {
                try
                {
                    var empty = Guid.Empty;
                    vol.SetMasterVolumeLevelScalar(level, ref empty);
                }
                catch (Exception ex)
                {
                    Log.Write($"SetVolume failed: {ex.Message}");
                }
            }
        }
    }

    public bool GetMute(bool input)
    {
        lock (_lock)
        {
            var vol = input ? _captureVolume : _renderVolume;
            if (vol is not null)
            {
                try
                {
                    if (vol.GetMute(out var mute) == 0)
                        return mute;
                }
                catch { }
            }
        }
        return false;
    }

    public void SetMute(bool input, bool mute)
    {
        lock (_lock)
        {
            var vol = input ? _captureVolume : _renderVolume;
            if (vol is not null)
            {
                try
                {
                    var empty = Guid.Empty;
                    vol.SetMute(mute, ref empty);
                }
                catch (Exception ex)
                {
                    Log.Write($"SetMute failed: {ex.Message}");
                }
            }
        }
    }

    public bool SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);

            RefreshEndpointVolumes();
            DevicesChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to set default audio device to '{deviceId}'", ex);
            return false;
        }
    }

    private void OnEndpointVolumeChanged(bool isInput)
    {
        float level = GetVolume(isInput);
        bool mute = GetMute(isInput);
        VolumeChanged?.Invoke(isInput, level, mute);
    }

    private void OnEndpointsChanged()
    {
        RefreshEndpointVolumes();
        DevicesChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_renderVolume is not null && _renderVolumeCallback is not null)
            {
                try { _renderVolume.UnregisterControlChangeNotify(_renderVolumeCallback); } catch { }
                _renderVolume = null;
            }
            if (_captureVolume is not null && _captureVolumeCallback is not null)
            {
                try { _captureVolume.UnregisterControlChangeNotify(_captureVolumeCallback); } catch { }
                _captureVolume = null;
            }
            if (_enumerator is not null && _notificationClient is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
                _notificationClient = null;
            }
            _enumerator = null;
        }
    }

    private sealed class NotificationClient(AudioService service) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => service.OnEndpointsChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => service.OnEndpointsChanged();
        public void OnDeviceRemoved(string pwstrDeviceId) => service.OnEndpointsChanged();
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId) => service.OnEndpointsChanged();
        public void OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) => service.OnEndpointsChanged();
    }

    private sealed class VolumeCallback(AudioService service, bool isInput) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr pNotify)
        {
            service.OnEndpointVolumeChanged(isInput);
            return 0;
        }
    }
}
