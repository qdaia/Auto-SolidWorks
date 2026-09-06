using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal sealed class SolidWorksStartupAudioSilencer : IDisposable
{
    private static readonly Guid AudioSessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid VolumeEventContext = new("5B38731B-1F15-49B0-AFA4-12361978DB7D");

    private readonly DateTime _startedUtc;
    private readonly object _gate = new();
    private readonly Dictionary<string, MutedSession> _sessions = new(StringComparer.Ordinal);
    private IMMDeviceEnumerator? _deviceEnumerator;
    private IMMDevice? _device;
    private IAudioSessionManager2? _sessionManager;
    private AudioSessionNotification? _notification;
    private bool _disposed;

    private SolidWorksStartupAudioSilencer(DateTime startedUtc)
    {
        _startedUtc = startedUtc;
    }

    public bool IsArmed { get; private set; }
    public bool RestoreSucceeded { get; private set; } = true;

    public static SolidWorksStartupAudioSilencer Start(DateTime startedUtc)
    {
        var silencer = new SolidWorksStartupAudioSilencer(startedUtc);
        silencer.Initialize();
        return silencer;
    }

    private void Initialize()
    {
        _deviceEnumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
        Marshal.ThrowExceptionForHR(_deviceEnumerator.GetDefaultAudioEndpoint(
            AudioDataFlow.Render, AudioRole.Multimedia, out _device));

        var audioSessionManager2Id = AudioSessionManager2Id;
        Marshal.ThrowExceptionForHR(_device.Activate(
            ref audioSessionManager2Id, 23, IntPtr.Zero, out var activated));
        _sessionManager = (IAudioSessionManager2)activated;

        // Microsoft requires GetCount before registration or new-session callbacks may be discarded.
        Marshal.ThrowExceptionForHR(_sessionManager.GetSessionEnumerator(out var enumerator));
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetCount(out _));
        }
        finally
        {
            ReleaseCom(enumerator);
        }

        _notification = new AudioSessionNotification(this);
        Marshal.ThrowExceptionForHR(_sessionManager.RegisterSessionNotification(_notification));
        MuteMatchingSessions();
    }

    public void MuteMatchingSessions()
    {
        if (_disposed || _sessionManager is null) return;
        IAudioSessionEnumerator? enumerator = null;
        try
        {
            Marshal.ThrowExceptionForHR(_sessionManager.GetSessionEnumerator(out enumerator));
            Marshal.ThrowExceptionForHR(enumerator.GetCount(out var count));
            for (var index = 0; index < count; index++)
            {
                try
                {
                    IAudioSessionControl? control = null;
                    if (enumerator.GetSession(index, out control) >= 0)
                        TryMute(control);
                }
                catch (COMException) { }
            }
        }
        catch (COMException) { }
        finally
        {
            ReleaseCom(enumerator);
        }
    }

    internal void TryMute(IAudioSessionControl control)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var control2 = (IAudioSessionControl2)control;
                var isSystemSounds = control2.IsSystemSoundsSession() == 0;
                var isNewSolidWorks = false;
                if (!isSystemSounds && control2.GetProcessId(out var processId) >= 0)
                    isNewSolidWorks = IsNewSolidWorksProcess(processId);
                if (!isSystemSounds && !isNewSolidWorks) return;

                Marshal.ThrowExceptionForHR(control2.GetSessionInstanceIdentifier(out var sessionId));
                if (_sessions.ContainsKey(sessionId)) return;

                var volume = (ISimpleAudioVolume)control;
                Marshal.ThrowExceptionForHR(volume.GetMute(out var wasMuted));
                if (!wasMuted)
                {
                    var eventContext = VolumeEventContext;
                    Marshal.ThrowExceptionForHR(volume.SetMute(true, ref eventContext));
                }

                _sessions.Add(sessionId, new(volume, wasMuted));
                IsArmed = true;
            }
            catch (COMException) { }
            catch (ArgumentException) { }
            catch (InvalidCastException) { }
        }
    }

    private bool IsNewSolidWorksProcess(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase) &&
                   process.StartTime.ToUniversalTime() >= _startedUtc - TimeSpan.FromSeconds(2);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_sessionManager is not null && _notification is not null)
            {
                try { _sessionManager.UnregisterSessionNotification(_notification); }
                catch (COMException) { }
            }

            foreach (var session in _sessions.Values)
            {
                if (!session.WasMuted)
                {
                    try
                    {
                        var eventContext = VolumeEventContext;
                        var result = session.Volume.SetMute(false, ref eventContext);
                        if (result < 0) RestoreSucceeded = false;
                    }
                    catch (COMException) { RestoreSucceeded = false; }
                    catch (InvalidComObjectException) { RestoreSucceeded = false; }
                }
            }
            _sessions.Clear();

            ReleaseCom(_sessionManager);
            ReleaseCom(_device);
            ReleaseCom(_deviceEnumerator);
            _notification = null;
            _sessionManager = null;
            _device = null;
            _deviceEnumerator = null;
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private sealed record MutedSession(ISimpleAudioVolume Volume, bool WasMuted);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AudioSessionNotification(SolidWorksStartupAudioSilencer owner) : IAudioSessionNotification
    {
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            owner.TryMute(newSession);
            return 0;
        }
    }
}

internal enum AudioDataFlow
{
    Render,
    Capture,
    All
}

internal enum AudioRole
{
    Console,
    Multimedia,
    Communications
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out object devices);
    [PreserveSig] int GetDefaultAudioEndpoint(AudioDataFlow dataFlow, AudioRole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters,
        [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    [PreserveSig] int OpenPropertyStore(uint storageAccess, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(ref Guid sessionId, uint streamFlags, out IAudioSessionControl control);
    [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionId, uint streamFlags, out ISimpleAudioVolume volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl control);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl methods must be repeated here in exact vtable order.
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingId);
    [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

    // IAudioSessionControl2 methods.
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceId);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IAudioSessionControl newSession);
}
