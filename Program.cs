using System.Runtime.InteropServices;

NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
NativeMethods.FreeConsole();

return SetRes.Run(args);

internal static class SetRes
{
    private const int ExitOk = 0;
    private const int ExitError = 1;

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_RESET = 0x40000000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    private const uint DM_BITSPERPEL = 0x00040000;
    private const uint DM_POSITION = 0x00000020;
    private const uint DM_DISPLAYORIENTATION = 0x00000080;
    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFLAGS = 0x00200000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;
    private const uint DM_DISPLAYFIXEDOUTPUT = 0x20000000;

    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    public static int Run(string[] args)
    {
        if (!TryResolveRequest(args, out var resolved))
        {
            return ExitError;
        }

        var newMode = resolved.NewMode;
        var result = NativeMethods.ChangeDisplaySettingsEx(
            resolved.Monitor.AdapterDeviceName,
            ref newMode,
            IntPtr.Zero,
            CDS_UPDATEREGISTRY | CDS_RESET,
            IntPtr.Zero);

        if (result != DISP_CHANGE_SUCCESSFUL)
        {
            AttachParentConsole();
            Console.Error.WriteLine(
                $"ChangeDisplaySettingsEx failed for {resolved.Monitor.MonitorName} ({resolved.Monitor.AdapterDeviceName}): {DisplayChangeResultName(result)} ({result}).");
            return ExitError;
        }

        AttachParentConsole();
        Console.WriteLine(
            $"{resolved.Monitor.MonitorName} ({resolved.Monitor.AdapterDeviceName}): {FormatMode(resolved.CurrentMode)} -> {FormatMode(resolved.NewMode)}");
        return ExitOk;
    }

    private static bool TryResolveRequest(string[] args, out ResolvedRequest resolved)
    {
        resolved = default;

        if (!TryParseArgs(args, out var request, out var error))
        {
            AttachParentConsole();
            Console.Error.WriteLine(error);
            PrintUsage();
            PrintDetectedMonitors(EnumerateMonitors());
            return false;
        }

        var monitors = EnumerateMonitors();
        var matches = monitors
            .Where(m => m.MonitorName.Contains(request.MonitorSubstring, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count != 1)
        {
            AttachParentConsole();
            Console.Error.WriteLine(matches.Count == 0
                ? $"No monitor friendly name matched \"{request.MonitorSubstring}\"."
                : $"Multiple monitor friendly names matched \"{request.MonitorSubstring}\".");
            Console.Error.WriteLine("No changes made.");
            PrintDetectedMonitors(monitors);
            return false;
        }

        var monitor = matches[0];

        if (!TryGetCurrentMode(monitor.AdapterDeviceName, out var currentMode))
        {
            AttachParentConsole();
            Console.Error.WriteLine($"Could not read current mode for {monitor.AdapterDeviceName} ({monitor.MonitorName}).");
            return false;
        }

        var availableModes = EnumerateModes(monitor.AdapterDeviceName).ToList();
        var matchingMode = availableModes.FirstOrDefault(m =>
            m.dmPelsWidth == request.Width &&
            m.dmPelsHeight == request.Height &&
            m.dmDisplayFrequency == request.Hz);

        if (matchingMode.dmSize == 0)
        {
            AttachParentConsole();
            Console.Error.WriteLine(
                $"Mode {request.Width}x{request.Height}@{request.Hz}Hz was not found for {monitor.MonitorName} ({monitor.AdapterDeviceName}).");
            PrintAvailableModes(monitor, availableModes);
            return false;
        }

        var newMode = currentMode;
        newMode.dmBitsPerPel = matchingMode.dmBitsPerPel;
        newMode.dmPelsWidth = matchingMode.dmPelsWidth;
        newMode.dmPelsHeight = matchingMode.dmPelsHeight;
        newMode.dmDisplayFlags = matchingMode.dmDisplayFlags;
        newMode.dmDisplayFrequency = matchingMode.dmDisplayFrequency;
        newMode.dmFields = currentMode.dmFields |
            DM_POSITION |
            DM_DISPLAYORIENTATION |
            DM_BITSPERPEL |
            DM_PELSWIDTH |
            DM_PELSHEIGHT |
            DM_DISPLAYFLAGS |
            DM_DISPLAYFREQUENCY |
            DM_DISPLAYFIXEDOUTPUT;

        resolved = new ResolvedRequest(monitor, currentMode, newMode);
        return true;
    }

    private static void AttachParentConsole()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
    }

    private static bool TryParseArgs(string[] args, out Request request, out string error)
    {
        request = default;
        error = string.Empty;

        string? monitor = null;
        int? width = null;
        int? height = null;
        int? hz = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument: {arg}";
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = $"Missing value for {arg}.";
                return false;
            }

            var value = args[++i];
            switch (arg.ToLowerInvariant())
            {
                case "--monitor":
                    monitor = value;
                    break;
                case "--width":
                    if (!TryParsePositiveInt(value, out var parsedWidth))
                    {
                        error = $"Invalid --width value: {value}";
                        return false;
                    }

                    width = parsedWidth;
                    break;
                case "--height":
                    if (!TryParsePositiveInt(value, out var parsedHeight))
                    {
                        error = $"Invalid --height value: {value}";
                        return false;
                    }

                    height = parsedHeight;
                    break;
                case "--hz":
                    if (!TryParsePositiveInt(value, out var parsedHz))
                    {
                        error = $"Invalid --hz value: {value}";
                        return false;
                    }

                    hz = parsedHz;
                    break;
                default:
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(monitor) || width is null || height is null || hz is null)
        {
            error = "All arguments are required.";
            return false;
        }

        request = new Request(monitor, width.Value, height.Value, hz.Value);
        return true;
    }

    private static bool TryParsePositiveInt(string value, out int parsed)
    {
        return int.TryParse(value, out parsed) && parsed > 0;
    }

    private static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var result = ERROR_SUCCESS;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = NativeMethods.GetDisplayConfigBufferSizes(
                QDC_ONLY_ACTIVE_PATHS,
                out var pathCount,
                out var modeCount);

            if (result != ERROR_SUCCESS)
            {
                Console.Error.WriteLine($"GetDisplayConfigBufferSizes failed: {Win32ErrorName(result)} ({result}).");
                return monitors;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[(int)pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[(int)modeCount];

            result = NativeMethods.QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                IntPtr.Zero);

            if (result == ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (result != ERROR_SUCCESS)
            {
                Console.Error.WriteLine($"QueryDisplayConfig failed: {Win32ErrorName(result)} ({result}).");
                return monitors;
            }

            for (var i = 0; i < (int)pathCount; i++)
            {
                var path = paths[i];
                var targetName = DISPLAYCONFIG_TARGET_DEVICE_NAME.Create(
                    DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    path.targetInfo.adapterId,
                    path.targetInfo.id);

                result = NativeMethods.DisplayConfigGetDeviceInfo(ref targetName);
                if (result != ERROR_SUCCESS)
                {
                    Console.Error.WriteLine(
                        $"DisplayConfigGetDeviceInfo target-name failed for target {path.targetInfo.id}: {Win32ErrorName(result)} ({result}).");
                    continue;
                }

                var sourceName = DISPLAYCONFIG_SOURCE_DEVICE_NAME.Create(
                    DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    path.sourceInfo.adapterId,
                    path.sourceInfo.id);

                result = NativeMethods.DisplayConfigGetDeviceInfo(ref sourceName);
                if (result != ERROR_SUCCESS)
                {
                    Console.Error.WriteLine(
                        $"DisplayConfigGetDeviceInfo source-name failed for source {path.sourceInfo.id}: {Win32ErrorName(result)} ({result}).");
                    continue;
                }

                var friendlyName = string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName)
                    ? "(unknown monitor)"
                    : targetName.monitorFriendlyDeviceName;

                monitors.Add(new MonitorInfo(sourceName.viewGdiDeviceName, friendlyName));
            }

            return monitors;
        }

        Console.Error.WriteLine($"QueryDisplayConfig failed after retrying changed buffers: {Win32ErrorName(result)} ({result}).");
        return monitors;
    }

    private static bool TryGetCurrentMode(string adapterDeviceName, out DEVMODE mode)
    {
        mode = DEVMODE.Create();
        return NativeMethods.EnumDisplaySettingsEx(adapterDeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0);
    }

    private static IEnumerable<DEVMODE> EnumerateModes(string adapterDeviceName)
    {
        for (var modeIndex = 0; ; modeIndex++)
        {
            var mode = DEVMODE.Create();
            if (!NativeMethods.EnumDisplaySettingsEx(adapterDeviceName, modeIndex, ref mode, 0))
            {
                yield break;
            }

            yield return mode;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: setres --monitor <name substring> --width <int> --height <int> --hz <int>");
    }

    private static void PrintDetectedMonitors(IReadOnlyCollection<MonitorInfo> monitors)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Detected monitors:");
        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("  (none)");
            return;
        }

        foreach (var monitor in monitors)
        {
            Console.Error.WriteLine($"  {monitor.MonitorName} | GDI {monitor.AdapterDeviceName}");
        }
    }

    private static void PrintAvailableModes(MonitorInfo monitor, IReadOnlyCollection<DEVMODE> modes)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Available modes for {monitor.MonitorName} ({monitor.AdapterDeviceName}):");
        if (modes.Count == 0)
        {
            Console.Error.WriteLine("  (none)");
            return;
        }

        foreach (var mode in modes
                     .Select(m => new { m.dmPelsWidth, m.dmPelsHeight, m.dmDisplayFrequency, m.dmBitsPerPel })
                     .Distinct()
                     .OrderBy(m => m.dmPelsWidth)
                     .ThenBy(m => m.dmPelsHeight)
                     .ThenBy(m => m.dmDisplayFrequency)
                     .ThenBy(m => m.dmBitsPerPel))
        {
            Console.Error.WriteLine(
                $"  {mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency}Hz {mode.dmBitsPerPel}bpp");
        }
    }

    private static string FormatMode(DEVMODE mode)
    {
        return $"{mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency}Hz {mode.dmBitsPerPel}bpp";
    }

    private static string DisplayChangeResultName(int result)
    {
        return result switch
        {
            0 => "DISP_CHANGE_SUCCESSFUL",
            1 => "DISP_CHANGE_RESTART",
            -1 => "DISP_CHANGE_FAILED",
            -2 => "DISP_CHANGE_BADMODE",
            -3 => "DISP_CHANGE_NOTUPDATED",
            -4 => "DISP_CHANGE_BADFLAGS",
            -5 => "DISP_CHANGE_BADPARAM",
            -6 => "DISP_CHANGE_BADDUALVIEW",
            _ => "UNKNOWN"
        };
    }

    private static string Win32ErrorName(int result)
    {
        return result switch
        {
            ERROR_SUCCESS => "ERROR_SUCCESS",
            5 => "ERROR_ACCESS_DENIED",
            31 => "ERROR_GEN_FAILURE",
            50 => "ERROR_NOT_SUPPORTED",
            87 => "ERROR_INVALID_PARAMETER",
            ERROR_INSUFFICIENT_BUFFER => "ERROR_INSUFFICIENT_BUFFER",
            _ => "UNKNOWN"
        };
    }

    private readonly record struct Request(string MonitorSubstring, int Width, int Height, int Hz);

    private readonly record struct MonitorInfo(string AdapterDeviceName, string MonitorName);

    private readonly record struct ResolvedRequest(MonitorInfo Monitor, DEVMODE CurrentMode, DEVMODE NewMode);
}

internal static partial class NativeMethods
{
    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsEx(
        string? lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode,
        uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct DEVMODE
{
    public fixed char dmDeviceName[32];

    public ushort dmSpecVersion;
    public ushort dmDriverVersion;
    public ushort dmSize;
    public ushort dmDriverExtra;
    public uint dmFields;

    public int dmPositionX;
    public int dmPositionY;
    public uint dmDisplayOrientation;
    public uint dmDisplayFixedOutput;

    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;

    public fixed char dmFormName[32];

    public ushort dmLogPixels;
    public uint dmBitsPerPel;
    public uint dmPelsWidth;
    public uint dmPelsHeight;
    public uint dmDisplayFlags;
    public uint dmDisplayFrequency;
    public uint dmICMMethod;
    public uint dmICMIntent;
    public uint dmMediaType;
    public uint dmDitherType;
    public uint dmReserved1;
    public uint dmReserved2;
    public uint dmPanningWidth;
    public uint dmPanningHeight;

    public static DEVMODE Create()
    {
        return new DEVMODE
        {
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>(),
            dmDriverExtra = 0
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;

    public static DISPLAYCONFIG_TARGET_DEVICE_NAME Create(uint type, LUID adapterId, uint id)
    {
        return new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = type,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = id
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;

    public static DISPLAYCONFIG_SOURCE_DEVICE_NAME Create(uint type, LUID adapterId, uint id)
    {
        return new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = type,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = id
            },
            viewGdiDeviceName = string.Empty
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_2DREGION
{
    public uint cx;
    public uint cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECTL
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize;
    public DISPLAYCONFIG_2DREGION totalSize;
    public uint videoStandard;
    public uint scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public uint pixelFormat;
    public POINTL position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
{
    public POINTL PathSourceSize;
    public RECTL DesktopImageRegion;
    public RECTL DesktopImageClip;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DISPLAYCONFIG_MODE_INFO_UNION
{
    [FieldOffset(0)]
    public DISPLAYCONFIG_TARGET_MODE targetMode;

    [FieldOffset(0)]
    public DISPLAYCONFIG_SOURCE_MODE sourceMode;

    [FieldOffset(0)]
    public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
}
