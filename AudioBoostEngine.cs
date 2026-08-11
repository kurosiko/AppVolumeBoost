using NAudio.CoreAudioApi;
using System.IO;
using System.Runtime.InteropServices;

namespace AppVolumeBoost;

/// <summary>
/// Routes one process to a dedicated Voicemeeter virtual input and changes only
/// that input strip's gain. This avoids the broken process-loopback activation
/// path on recent Windows 11 builds.
/// </summary>
public sealed class AudioBoostEngine : IDisposable
{
    private readonly object _sync = new();
    private VoicemeeterRemote? _remote;
    private int? _activeProcessId;
    private string? _restoreEndpointId;
    private int _stripIndex;

    public int? ActiveProcessId
    {
        get { lock (_sync) return _activeProcessId; }
    }

    public void SetGainDb(double gainDb)
    {
        var remote = _remote;
        if (remote is not null)
            remote.SetBoostGain(_stripIndex, Math.Clamp(gainDb, 0, AudioBoostMath.MaxGainDb));
    }

    public async Task StartAsync(AudioAppViewModel app, double gainDb)
    {
        await StopAsync().ConfigureAwait(false);
        DebugLog.Write($"Voicemeeter start requested pid={app.ProcessId} gainDb={gainDb:0.0}");

        VoicemeeterRemote? remote = null;
        try
        {
            remote = new VoicemeeterRemote();
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var virtualDevice = FindVirtualInput(enumerator, remote);

            _restoreEndpointId = defaultDevice.ID;
            _stripIndex = remote.VirtualStripIndex;

            // The policy API applies the endpoint to the process's new/current audio stream.
            AudioPolicyRouter.SetRenderEndpoint(app.ProcessId, virtualDevice.ID);
            remote.SetBoostGain(_stripIndex, Math.Clamp(gainDb, 0, AudioBoostMath.MaxGainDb));
            remote.SetHardwareRoute(_stripIndex, true);

            _remote = remote;
            lock (_sync) _activeProcessId = app.ProcessId;
            DebugLog.Write($"Voicemeeter routed pid={app.ProcessId} device={virtualDevice.FriendlyName} strip={_stripIndex}");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Voicemeeter start failed pid={app.ProcessId} error={ex.GetType().Name}: {ex.Message}");
            try
            {
                if (_restoreEndpointId is not null)
                    AudioPolicyRouter.SetRenderEndpoint(app.ProcessId, _restoreEndpointId);
            }
            catch { }
            remote?.Dispose();
            _restoreEndpointId = null;
            throw new InvalidOperationException(
                "Voicemeeter方式を開始できませんでした。Voicemeeterを起動し、A1に実際のスピーカー／ヘッドホンを設定してください。", ex);
        }
    }

    public Task StopAsync()
    {
        var pid = ActiveProcessId;
        var restoreId = _restoreEndpointId;
        var remote = _remote;

        if (pid is int processId && restoreId is not null)
        {
            try { AudioPolicyRouter.SetRenderEndpoint(processId, restoreId); }
            catch (Exception ex) { DebugLog.Write($"Endpoint restore failed pid={processId}: {ex.Message}"); }
        }

        try { remote?.RestoreStrip(_stripIndex); }
        catch (Exception ex) { DebugLog.Write($"Voicemeeter restore failed: {ex.Message}"); }
        finally { remote?.Dispose(); }

        _remote = null;
        _restoreEndpointId = null;
        lock (_sync) _activeProcessId = null;
        return Task.CompletedTask;
    }

    private static MMDevice FindVirtualInput(MMDeviceEnumerator enumerator, VoicemeeterRemote remote)
    {
        var expected = remote.VirtualInputName;
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            if (device.FriendlyName.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return device;
            device.Dispose();
        }

        devices.Dispose();
        throw new InvalidOperationException($"Voicemeeterの仮想入力 '{expected}' が見つかりません。");
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

internal sealed class VoicemeeterRemote : IDisposable
{
    private const string InstallFolder = @"C:\Program Files (x86)\VB\Voicemeeter";
    private nint _module;
    private bool _loggedIn;
    private float? _oldGain;
    private float? _oldA1;

    private delegate int LoginDelegate();
    private delegate int LogoutDelegate();
    private delegate int GetTypeDelegate(ref int value);
    private delegate int GetFloatDelegate([MarshalAs(UnmanagedType.LPStr)] string name, out float value);
    private delegate int SetFloatDelegate([MarshalAs(UnmanagedType.LPStr)] string name, float value);
    private delegate int SetParametersDelegate([MarshalAs(UnmanagedType.LPStr)] string script);

    private readonly LoginDelegate _login;
    private readonly LogoutDelegate _logout;
    private readonly GetTypeDelegate _getType;
    private readonly GetFloatDelegate _getFloat;
    private readonly SetFloatDelegate _setFloat;
    private readonly SetParametersDelegate _setParameters;

    public int Type { get; }
    public int VirtualStripIndex { get; }
    public string VirtualInputName { get; }

    public VoicemeeterRemote()
    {
        var dll = Path.Combine(InstallFolder, Environment.Is64BitProcess ? "VoicemeeterRemote64.dll" : "VoicemeeterRemote.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("VoicemeeterRemote.dllが見つかりません。", dll);

        _module = NativeLibrary.Load(dll);
        try
        {
            _login = Load<LoginDelegate>("VBVMR_Login");
            _logout = Load<LogoutDelegate>("VBVMR_Logout");
            _getType = Load<GetTypeDelegate>("VBVMR_GetVoicemeeterType");
            _getFloat = Load<GetFloatDelegate>("VBVMR_GetParameterFloat");
            _setFloat = Load<SetFloatDelegate>("VBVMR_SetParameterFloat");
            _setParameters = Load<SetParametersDelegate>("VBVMR_SetParameters");

            var result = _login();
            if (result != 0)
                throw new InvalidOperationException($"Voicemeeter Remote API Login failed ({result})");
            _loggedIn = true;

            var type = 0;
            Check(_getType(ref type), "GetVoicemeeterType");
            Type = type;
            (VirtualStripIndex, VirtualInputName) = type switch
            {
                3 => (7, "Voicemeeter VAIO3 Input"),
                2 => (4, "Voicemeeter AUX Input"),
                _ => (2, "Voicemeeter Input")
            };
            DebugLog.Write($"Voicemeeter connected type={Type} strip={VirtualStripIndex}");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetBoostGain(int stripIndex, double gainDb)
    {
        var prefix = $"Strip[{stripIndex}]";
        _oldGain ??= Get(prefix + ".gain");
        _oldA1 ??= Get(prefix + ".A1");
        SetGain(prefix, gainDb);
        Check(_setFloat(prefix + ".A1", 1), "Set strip A1");
    }

    public void SetHardwareRoute(int stripIndex, bool enabled) =>
        Check(_setFloat($"Strip[{stripIndex}].A1", enabled ? 1 : 0), "Set strip A1");

    public void RestoreStrip(int stripIndex)
    {
        var prefix = $"Strip[{stripIndex}]";
        if (_oldGain is float gain) SetGain(prefix, gain);
        if (_oldA1 is float a1) _setFloat(prefix + ".A1", a1);
        _oldGain = null;
        _oldA1 = null;
    }

    private void SetGain(string prefix, double gainDb)
    {
        if (gainDb <= 12)
        {
            Check(_setFloat(prefix + ".gain", (float)gainDb), "Set strip gain");
            return;
        }

        // Voicemeeter's ordinary gain setter stops at +12 dB. Its parameter
        // script supports relative gain changes beyond the visible slider range.
        var extraDb = gainDb - 12;
        var script = $"{prefix}.gain=12.0;{prefix}.gain+={extraDb:0.###};";
        Check(_setParameters(script), "Set extended strip gain");
    }

    private float Get(string name)
    {
        Check(_getFloat(name, out var value), "Get " + name);
        return value;
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Voicemeeter API {operation} failed ({result})");
    }

    public void Dispose()
    {
        if (_module == 0) return;
        try
        {
            if (_loggedIn) _logout();
        }
        catch { }
        finally
        {
            _loggedIn = false;
            NativeLibrary.Free(_module);
            _module = 0;
        }
    }
}

internal static class AudioPolicyRouter
{
    private enum Flow { Render = 0, Capture = 1 }
    private enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    private static readonly Guid Factory21H2 = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid FactoryDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");
    private const string MmDeviceApiToken = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEndpointDelegate(IntPtr @this, uint processId, Flow flow, Role role, IntPtr deviceId);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr classId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public static void SetRenderEndpoint(int processId, string deviceId)
    {
        var deviceHString = IntPtr.Zero;
        var classHString = IntPtr.Zero;
        var packedDeviceId = MmDeviceApiToken + deviceId + RenderInterface;
        WindowsCreateString(packedDeviceId, (uint)packedDeviceId.Length, out deviceHString);
        const string classId = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(classId, (uint)classId.Length, out classHString);
        try
        {
            var iid = Environment.OSVersion.Version.Build >= 21390
                ? Factory21H2
                : FactoryDownlevel;
            var hr = RoGetActivationFactory(classHString, ref iid, out var factoryPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var result = InvokeSetEndpoint(factoryPtr, processId, Role.Console, deviceHString);
                if (result != 0) Marshal.ThrowExceptionForHR(result);
                result = InvokeSetEndpoint(factoryPtr, processId, Role.Multimedia, deviceHString);
                if (result != 0) Marshal.ThrowExceptionForHR(result);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            if (deviceHString != IntPtr.Zero) WindowsDeleteString(deviceHString);
            if (classHString != IntPtr.Zero) WindowsDeleteString(classHString);
        }
    }

    private static int InvokeSetEndpoint(IntPtr factory, int processId, Role role, IntPtr deviceId)
    {
        // IInspectable contributes 3 vtable entries after IUnknown. The internal
        // factory has 19 methods before SetPersistedDefaultAudioEndpoint.
        var vtable = Marshal.ReadIntPtr(factory);
        var method = Marshal.ReadIntPtr(vtable, IntPtr.Size * (3 + 3 + 19));
        var call = Marshal.GetDelegateForFunctionPointer<SetEndpointDelegate>(method);
        return call(factory, unchecked((uint)processId), Flow.Render, role, deviceId);
    }
}
