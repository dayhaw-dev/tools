using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

[assembly: SupportedOSPlatform("windows")]

NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

return AudioSwap.Run(args);

internal static class AudioSwap
{
    private const int ExitOk = 0;
    private const int ExitError = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static int Run(string[] args)
    {
        if (args.Length != 0)
        {
            PrintUsage();
            return ExitError;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "audioswap.json");
        if (!TryLoadConfig(configPath, out var config))
        {
            return ExitError;
        }

        try
        {
            var devices = AudioManager.EnumerateActiveRenderDevicesForDiagnostics();
            var current = AudioManager.TryGetDefaultRenderDevice(devices, out var defaultError);
            var oldName = current?.FriendlyName ?? "(unknown default playback device)";
            var targetMatch = ResolveTargetMatch(config, current);
            var target = devices.FirstOrDefault(device =>
                device.FriendlyName.Contains(targetMatch, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                Console.Error.WriteLine($"target: no active playback device matched '{targetMatch}'. No changes made.");
                if (!string.IsNullOrWhiteSpace(defaultError))
                {
                    Console.Error.WriteLine($"default: {defaultError}");
                }

                PrintAvailableDevices(devices);
                return ExitError;
            }

            var policyResult = PolicyConfigFallback.SetDefaultEndpoints(target.Id);
            if (!policyResult.Success)
            {
                Console.Error.WriteLine($"audio: SetDefaultEndpoint failed for {target.FriendlyName}: {policyResult.Message}");
                return ExitError;
            }

            Console.WriteLine($"{oldName} -> {target.FriendlyName}");
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(AudioDiagnostics.FormatException("audio swap", ex));
            return ExitError;
        }
    }

    private static string ResolveTargetMatch(AudioSwapConfig config, AudioDeviceInfo? current)
    {
        if (current?.FriendlyName.Contains(config.DeviceA, StringComparison.OrdinalIgnoreCase) == true)
        {
            return config.DeviceB;
        }

        if (current?.FriendlyName.Contains(config.DeviceB, StringComparison.OrdinalIgnoreCase) == true)
        {
            return config.DeviceA;
        }

        return config.DeviceA;
    }

    private static bool TryLoadConfig(string configPath, out AudioSwapConfig config)
    {
        config = default!;
        try
        {
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"config: missing {configPath}");
                PrintUsage();
                return false;
            }

            var loaded = JsonSerializer.Deserialize<AudioSwapConfig>(File.ReadAllText(configPath), JsonOptions);
            if (loaded is null)
            {
                Console.Error.WriteLine("config: invalid or empty audioswap.json");
                return false;
            }

            if (!loaded.IsValid(out var error))
            {
                Console.Error.WriteLine($"config: {error}");
                return false;
            }

            config = loaded;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config: failed to read {configPath}: {ex.Message}");
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: audioswap");
        Console.Error.WriteLine("Place audioswap.json next to audioswap.exe before running.");
    }

    private static void PrintAvailableDevices(IReadOnlyCollection<AudioDeviceInfo> devices)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Available active playback devices:");
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("  (none)");
            return;
        }

        foreach (var device in devices)
        {
            Console.Error.WriteLine($"  {device.FriendlyName}");
        }
    }
}

internal sealed record AudioSwapConfig(string DeviceA, string DeviceB)
{
    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(DeviceA))
        {
            error = "missing deviceA";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DeviceB))
        {
            error = "missing deviceB";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

internal static class AudioManager
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint StgmRead = 0x00000000;
    private static readonly PROPERTYKEY PKeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PROPERTYKEY PKeyDeviceInterfaceFriendlyName = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

    public static List<AudioDeviceInfo> EnumerateActiveRenderDevicesForDiagnostics()
    {
        try
        {
            return EnumerateActiveRenderDevicesFromCom();
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            Console.WriteLine($"audio: COM MMDevice enumeration failed, falling back to registry - {AudioDiagnostics.FormatException("COM enumerate active render devices", ex)}");
            return EnumerateActiveRenderDevicesFromRegistry();
        }
    }

    public static AudioDeviceInfo? TryGetDefaultRenderDevice(IReadOnlyCollection<AudioDeviceInfo> devices, out string error)
    {
        error = string.Empty;
        try
        {
            var defaultDevice = GetDefaultRenderDeviceFromCom();
            return devices.FirstOrDefault(device => AudioIdsEqual(device.Id, defaultDevice.Id)) ?? defaultDevice;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            error = AudioDiagnostics.FormatException("COM get default render device", ex);
            return null;
        }
    }

    private static List<AudioDeviceInfo> EnumerateActiveRenderDevicesFromCom()
    {
        var result = new List<AudioDevice>();
        var enumerator = ComFactory.CreateMmDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateActive, out var collectionPointer));
        var collection = ComFactory.FromIUnknown<IMMDeviceCollection>(collectionPointer);
        Marshal.Release(collectionPointer);
        Marshal.ThrowExceptionForHR(collection.GetCount(out var count));

        for (uint i = 0; i < count; i++)
        {
            Marshal.ThrowExceptionForHR(collection.Item(i, out var devicePointer));
            var device = ComFactory.FromIUnknown<IMMDevice>(devicePointer);
            Marshal.Release(devicePointer);
            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out var propertyStorePointer));
                var propertyStore = ComFactory.FromIUnknown<IPropertyStore>(propertyStorePointer);
                Marshal.Release(propertyStorePointer);
                try
                {
                    var friendlyName = TryGetPropertyString(propertyStore, PKeyDeviceInterfaceFriendlyName);
                    friendlyName ??= TryGetPropertyString(propertyStore, PKeyDeviceFriendlyName);
                    result.Add(new AudioDevice(id, friendlyName ?? id));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(propertyStore);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(device);
            }
        }

        Marshal.FinalReleaseComObject(collection);
        Marshal.FinalReleaseComObject(enumerator);
        return result.Select(device => new AudioDeviceInfo(device.Id, device.FriendlyName)).ToList();
    }

    private static List<AudioDeviceInfo> EnumerateActiveRenderDevicesFromRegistry()
    {
        const string renderDevicesKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        var result = new List<AudioDeviceInfo>();
        using var renderDevicesKey = Registry.LocalMachine.OpenSubKey(renderDevicesKeyPath);
        if (renderDevicesKey is null)
        {
            return result;
        }

        foreach (var deviceId in renderDevicesKey.GetSubKeyNames())
        {
            using var deviceKey = renderDevicesKey.OpenSubKey(deviceId);
            if (deviceKey is null || !IsActiveDevice(deviceKey.GetValue("DeviceState")))
            {
                continue;
            }

            using var propertiesKey = deviceKey.OpenSubKey("Properties");
            var friendlyName = propertiesKey?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
            friendlyName ??= propertiesKey?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14") as string;
            var endpointId = $"{{0.0.0.00000000}}.{deviceId}";
            result.Add(new AudioDeviceInfo(endpointId, friendlyName ?? endpointId));
        }

        return result;
    }

    private static AudioDeviceInfo GetDefaultRenderDeviceFromCom()
    {
        var enumerator = ComFactory.CreateMmDeviceEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var devicePointer));
            var device = ComFactory.FromIUnknown<IMMDevice>(devicePointer);
            Marshal.Release(devicePointer);
            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out var propertyStorePointer));
                var propertyStore = ComFactory.FromIUnknown<IPropertyStore>(propertyStorePointer);
                Marshal.Release(propertyStorePointer);
                try
                {
                    var friendlyName = TryGetPropertyString(propertyStore, PKeyDeviceInterfaceFriendlyName);
                    friendlyName ??= TryGetPropertyString(propertyStore, PKeyDeviceFriendlyName);
                    return new AudioDeviceInfo(id, friendlyName ?? id);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(propertyStore);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private static bool IsActiveDevice(object? deviceState)
    {
        return deviceState switch
        {
            int value => value == DeviceStateActive,
            uint value => value == DeviceStateActive,
            _ => false
        };
    }

    private static string? TryGetPropertyString(IPropertyStore propertyStore, PROPERTYKEY key)
    {
        var localKey = key;
        var hr = propertyStore.GetValue(ref localKey, out var value);
        if (hr != 0)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    private static bool AudioIdsEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AudioDevice(string Id, string FriendlyName);
}

internal static class ComFactory
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    public static IMMDeviceEnumerator CreateMmDeviceEnumerator()
    {
        return CreateTypedComObject<IMMDeviceEnumerator>(MMDeviceEnumeratorClsid);
    }

    public static TPolicyConfig CreatePolicyConfig<TPolicyConfig>()
    {
        return CreateTypedComObject<TPolicyConfig>(PolicyConfigClientClsid);
    }

    public static TInterface FromIUnknown<TInterface>(IntPtr unknown)
    {
        return (TInterface)Marshal.GetTypedObjectForIUnknown(unknown, typeof(TInterface));
    }

    private static TInterface CreateTypedComObject<TInterface>(Guid clsid)
    {
        var iid = typeof(TInterface).GUID;
        var pointer = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CoCreateInstance(
                ref clsid,
                IntPtr.Zero,
                NativeMethods.CLSCTX_ALL,
                ref iid,
                out pointer));

            return (TInterface)Marshal.GetTypedObjectForIUnknown(pointer, typeof(TInterface));
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }
    }
}

internal static class PolicyConfigFallback
{
    public static PolicyConfigResult SetDefaultEndpoints(string deviceId)
    {
        var attempts = new List<string>();
        if (TrySetDefault<IPolicyConfigWin7>("IPolicyConfigWin7", deviceId, out var result))
        {
            return result;
        }

        attempts.Add(result.Message);
        if (TrySetDefault<IPolicyConfigWin10Th2>("IPolicyConfigWin10Th2", deviceId, out result))
        {
            return result;
        }

        attempts.Add(result.Message);
        if (TrySetDefault<IPolicyConfigWin10Th1>("IPolicyConfigWin10Th1", deviceId, out result))
        {
            return result;
        }

        attempts.Add(result.Message);
        return PolicyConfigResult.Fail(string.Join("; ", attempts));
    }

    private static bool TrySetDefault<TPolicyConfig>(string interfaceName, string deviceId, out PolicyConfigResult result)
    {
        object? policyConfig = null;
        try
        {
            var typedPolicyConfig = ComFactory.CreatePolicyConfig<TPolicyConfig>();
            policyConfig = typedPolicyConfig;

            var consoleResult = SetDefaultEndpoint(typedPolicyConfig, deviceId, ERole.eConsole);
            var multimediaResult = SetDefaultEndpoint(typedPolicyConfig, deviceId, ERole.eMultimedia);
            result = consoleResult == 0 && multimediaResult == 0
                ? PolicyConfigResult.Ok(interfaceName)
                : PolicyConfigResult.Fail($"{interfaceName}: console={consoleResult}, multimedia={multimediaResult}");
            return result.Success;
        }
        catch (Exception ex)
        {
            result = PolicyConfigResult.Fail($"{interfaceName}: {AudioDiagnostics.FormatException("policy config", ex)}");
            return false;
        }
        finally
        {
            if (policyConfig is not null && Marshal.IsComObject(policyConfig))
            {
                Marshal.FinalReleaseComObject(policyConfig);
            }
        }
    }

    private static int SetDefaultEndpoint<TPolicyConfig>(TPolicyConfig policyConfig, string deviceId, ERole role)
    {
        return policyConfig switch
        {
            IPolicyConfigWin7 win7 => win7.SetDefaultEndpoint(deviceId, role),
            IPolicyConfigWin10Th2 th2 => th2.SetDefaultEndpoint(deviceId, role),
            IPolicyConfigWin10Th1 th1 => th1.SetDefaultEndpoint(deviceId, role),
            _ => unchecked((int)0x80004002)
        };
    }
}

internal readonly record struct PolicyConfigResult(bool Success, string InterfaceName, string Message)
{
    public static PolicyConfigResult Ok(string interfaceName) => new(true, interfaceName, string.Empty);
    public static PolicyConfigResult Fail(string message) => new(false, string.Empty, message);
}

internal sealed record AudioDeviceInfo(string Id, string FriendlyName);

internal static class AudioDiagnostics
{
    public static string FormatException(string operation, Exception ex)
    {
        var stackLines = ex.StackTrace?
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(4)
            .ToArray() ?? [];
        var hresult = ex.HResult == 0 ? string.Empty : $" hresult=0x{ex.HResult:X8}";
        return $"{operation} failed: {ex.GetType().FullName}: {ex.Message}{hresult}" +
            (stackLines.Length == 0 ? string.Empty : $" stack={string.Join(" | ", stackLines)}");
    }
}

internal static partial class NativeMethods
{
    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
    internal const uint CLSCTX_ALL = 0x17;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint dwProcessId);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);
}

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr p;
    public int p2;

    public string? GetString()
    {
        return vt == 31 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
    }
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints([MarshalAs(UnmanagedType.I4)] EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint([MarshalAs(UnmanagedType.I4)] EDataFlow dataFlow, [MarshalAs(UnmanagedType.I4)] ERole role, out IntPtr ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-C0AF6C8DBA7A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IntPtr ppDevice);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigWin7
{
    [PreserveSig]
    int Unused1();

    [PreserveSig]
    int Unused2();

    [PreserveSig]
    int Unused3();

    [PreserveSig]
    int Unused4();

    [PreserveSig]
    int Unused5();

    [PreserveSig]
    int Unused6();

    [PreserveSig]
    int Unused7();

    [PreserveSig]
    int Unused8();

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PROPERTYKEY pkey, ref PROPVARIANT pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PROPERTYKEY pkey, ref PROPVARIANT pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, short isVisible);
}

[ComImport]
[Guid("6BE54BE8-A068-4875-A49D-0C2966473B11")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigWin10Th2
{
    [PreserveSig]
    int Unused1();

    [PreserveSig]
    int Unused2();

    [PreserveSig]
    int Unused3();

    [PreserveSig]
    int Unused4();

    [PreserveSig]
    int Unused5();

    [PreserveSig]
    int Unused6();

    [PreserveSig]
    int Unused7();

    [PreserveSig]
    int Unused8();

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PROPERTYKEY pkey, ref PROPVARIANT pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PROPERTYKEY pkey, ref PROPVARIANT pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, short isVisible);
}

[ComImport]
[Guid("CA286FC3-91FD-42C3-8E9B-CAAFA66242E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigWin10Th1
{
    [PreserveSig]
    int Unused1();

    [PreserveSig]
    int Unused2();

    [PreserveSig]
    int Unused3();

    [PreserveSig]
    int Unused4();

    [PreserveSig]
    int Unused5();

    [PreserveSig]
    int Unused6();

    [PreserveSig]
    int Unused7();

    [PreserveSig]
    int Unused8();

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PROPERTYKEY pkey, ref PROPVARIANT pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ref PROPERTYKEY pkey, ref PROPVARIANT pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole eRole);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, short isVisible);
}
