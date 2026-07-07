using System.Diagnostics;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: SupportedOSPlatform("windows")]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        return TvMode.RunAsync(args).GetAwaiter().GetResult();
    }
}

internal static class TvMode
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static readonly JsonSerializerOptions SamsungJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("input-direct", StringComparison.OrdinalIgnoreCase))
        {
            return await RunInputDirectDiagnosticAsync();
        }

        if (args.Length is 2 or 3 && args[0].Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            return await RunKeyDiagnosticAsync(args[1], args.Length == 3 ? args[2] : "Click");
        }

        if (args.Length == 2 && args[0].Equals("audio-repro", StringComparison.OrdinalIgnoreCase))
        {
            return RunAudioRepro(args[1]);
        }

        if (args.Length is < 1 or > 2 || !IsMode(args[0]))
        {
            PrintUsage();
            return ExitError;
        }

        var mode = args[0].ToLowerInvariant();
        var forceInput = false;
        if (args.Length == 2)
        {
            if (mode == "couch" && args[1].Equals("--force-input", StringComparison.OrdinalIgnoreCase))
            {
                forceInput = true;
            }
            else
            {
                PrintUsage();
                return ExitError;
            }
        }

        var exeDirectory = AppContext.BaseDirectory;
        var configPath = Path.Combine(exeDirectory, "tvmode.json");
        var windowStatePath = Path.Combine(exeDirectory, "tvmode.windows.json");
        if (!TryLoadConfig(configPath, out var config))
        {
            return ExitError;
        }

        if (mode == "desk")
        {
            var displayOk = RunStep("display", () => DisplayManager.SetPrimary(config.DeskDisplayMatch));
            RunStep("windows", () => WindowManager.RestoreFromState(windowStatePath));
            var audioOk = RunStep("audio", () => AudioManager.SetDefaultPlaybackDevice(config.DeskAudioMatch));
            return displayOk && audioOk ? ExitOk : ExitError;
        }

        var tokenPath = Path.Combine(exeDirectory, "tvmode.token");
        var workingTvIp = config.TvIp;
        var mustNavigateInput = forceInput;
        var canAssumeInputWhenOn = false;
        var applyWakeSettleDelay = false;
        var initialPower = await NetworkTools.QueryTvPowerStateAsync(config.TvIp);
        var tvOnline = false;

        if (initialPower.Reachable && initialPower.IsOn)
        {
            tvOnline = true;
            canAssumeInputWhenOn = true;
            Log("wake", true, $"path=on; REST PowerState={initialPower.StateForLog}; TV is already awake");
            Log("network", true, $"TV reachable at {workingTvIp}:8001 with PowerState={initialPower.StateForLog}");
        }
        else if (initialPower.Reachable)
        {
            mustNavigateInput = true;
            var powerKeySent = false;
            var confirmedOn = await RunStepAsync("wake", async () =>
            {
                var powerResult = await SamsungTvRemote.SendPowerKeyAsync(workingTvIp, tokenPath);
                if (!powerResult.Success)
                {
                    return StepResult.Fail($"path=fast-standby; REST PowerState={initialPower.StateForLog}; {powerResult.Message}");
                }

                powerKeySent = true;
                var waitResult = await NetworkTools.WaitForPowerStateOnAsync(workingTvIp, TimeSpan.FromSeconds(15));
                return waitResult.Success
                    ? StepResult.Ok($"path=fast-standby; initial REST PowerState={initialPower.StateForLog}; sent KEY_POWER; observed {waitResult.Message}")
                    : StepResult.Fail($"path=fast-standby; initial REST PowerState={initialPower.StateForLog}; sent KEY_POWER; {waitResult.Message}");
            });
            tvOnline = powerKeySent;
            applyWakeSettleDelay = powerKeySent;
            Log("network", tvOnline, confirmedOn
                ? $"TV reported PowerState=on at {workingTvIp}:8001 after KEY_POWER"
                : powerKeySent
                    ? $"continuing after KEY_POWER even though PowerState=on was not confirmed"
                    : $"TV did not wake because KEY_POWER was not sent");
        }
        else
        {
            mustNavigateInput = true;
            Log("wake", true, $"path=deep-standby; REST PowerState={initialPower.StateForLog}; sending WoL");
            await RunStepAsync("wol", () => NetworkTools.SendWakeOnLanAsync(config.TvMac));
            tvOnline = await RunStepAsync("network", async () =>
            {
                var resolved = await NetworkTools.ResolveTvIpAsync(config.TvIp, config.TvMac, TimeSpan.FromSeconds(30));
                workingTvIp = resolved.WorkingIp;
                return resolved.Success
                    ? StepResult.Ok(resolved.Warning ?? $"TV reachable at {resolved.WorkingIp}:8002 after WoL")
                    : StepResult.Fail(resolved.Warning ?? $"TV did not respond at {config.TvIp}:8002 and no ARP entry matched {config.TvMac}");
            });
            applyWakeSettleDelay = tvOnline;
        }

        if (tvOnline)
        {
            if (applyWakeSettleDelay)
            {
                await RunStepAsync("settle", async () =>
                {
                    await Task.Delay(config.WakeSettleDelay);
                    return StepResult.Ok($"waited {config.WakeSettleDelay.TotalMilliseconds:0}ms for Tizen UI to accept remote keys");
                });
            }

            var inputMethod = mustNavigateInput ? "keys" : config.InputMethod;
            if (!forceInput && !mustNavigateInput && config.AssumeInputWhenOnResolved && canAssumeInputWhenOn && config.ResolvedInputMethod == "keys")
            {
                Log("input", true, $"skipped KEY_SOURCE navigation because REST PowerState={initialPower.StateForLog} and assumeInputWhenOn is true; use --force-input to run it");
            }
            else
            {
                var reason = forceInput
                    ? "--force-input requested KEY_SOURCE navigation"
                    : mustNavigateInput
                        ? $"wake path requires KEY_SOURCE navigation; initial REST PowerState={initialPower.StateForLog}"
                        : $"TV was already on with REST PowerState={initialPower.StateForLog}, but navigation was not skipped";
                await RunStepAsync("input", async () =>
                {
                    var result = await SamsungTvRemote.SwitchInputAsync(
                        workingTvIp,
                        config.TvInput,
                        tokenPath,
                        inputMethod,
                        config.InterKeyDelay,
                        config.SourceBarOpenDelay,
                        config.TvInputLeftPresses,
                        config.TvInputRightPresses);
                    return result.Success
                        ? StepResult.Ok($"{result.Message}; {reason}")
                        : result;
                });
            }
        }
        else
        {
            Log("input", false, "skipped because TV was not reachable");
        }

        var couchDisplayOk = RunStep("display", () => DisplayManager.SetPrimary(config.CouchDisplayMatch));
        RunStep("windows", () => WindowManager.MinimizeOnDisplay(config.MinimizeDisplayMatch, windowStatePath));
        var couchAudioOk = RunStep("audio", () => AudioManager.SetDefaultPlaybackDevice(config.CouchAudioMatch));

        return couchDisplayOk && couchAudioOk ? ExitOk : ExitError;
    }

    private static bool IsMode(string value)
    {
        return value.Equals("couch", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("desk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryLoadConfig(string configPath, out TvModeConfig config)
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

            var loaded = JsonSerializer.Deserialize<TvModeConfig>(File.ReadAllText(configPath), JsonOptions);
            if (loaded is null)
            {
                Console.Error.WriteLine("config: invalid or empty tvmode.json");
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
        Console.Error.WriteLine("Usage: tvmode couch|desk");
        Console.Error.WriteLine("Usage: tvmode couch --force-input");
        Console.Error.WriteLine("Diagnostic: tvmode input-direct");
        Console.Error.WriteLine("Diagnostic: tvmode key <KEYNAME> [Click|PressRelease]");
        Console.Error.WriteLine("Diagnostic: tvmode audio-repro <audio device name substring>");
        Console.Error.WriteLine("Place tvmode.json next to tvmode.exe before running.");
    }

    private static async Task<int> RunInputDirectDiagnosticAsync()
    {
        var exeDirectory = AppContext.BaseDirectory;
        var configPath = Path.Combine(exeDirectory, "tvmode.json");
        if (!TryLoadConfig(configPath, out var config))
        {
            return ExitError;
        }

        var tokenPath = Path.Combine(exeDirectory, "tvmode.token");
        var result = await SamsungTvRemote.SendDirectInputDiagnosticAsync(config.TvIp, config.TvInput, tokenPath);
        Log("input-direct", result.Success, result.Message);
        return result.Success ? ExitOk : ExitError;
    }

    private static async Task<int> RunKeyDiagnosticAsync(string keyName, string mode)
    {
        var exeDirectory = AppContext.BaseDirectory;
        var configPath = Path.Combine(exeDirectory, "tvmode.json");
        if (!TryLoadConfig(configPath, out var config))
        {
            return ExitError;
        }

        var tokenPath = Path.Combine(exeDirectory, "tvmode.token");
        var result = await SamsungTvRemote.SendSingleKeyDiagnosticAsync(config.TvIp, keyName, tokenPath, mode);
        Log("key", result.Success, result.Message);
        return result.Success ? ExitOk : ExitError;
    }

    private static int RunAudioRepro(string audioMatch)
    {
        Console.WriteLine($"audio-repro: match '{audioMatch}'");
        try
        {
            var devices = AudioManager.EnumerateActiveRenderDevicesForDiagnostics();
            Console.WriteLine($"audio-repro: active render devices: {devices.Count}");
            foreach (var device in devices)
            {
                Console.WriteLine($"audio-repro: device '{device.FriendlyName}' id='{device.Id}'");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"audio-repro: enumerate failed - {AudioDiagnostics.FormatException("enumerate active render devices", ex)}");
            return ExitError;
        }

        var result = AudioManager.SetDefaultPlaybackDevice(audioMatch);
        Log("audio-repro", result.Success, result.Message);
        return result.Success ? ExitOk : ExitError;
    }

    private static bool RunStep(string name, Func<StepResult> step)
    {
        try
        {
            var result = step();
            Log(name, result.Success, result.Message);
            return result.Success;
        }
        catch (Exception ex)
        {
            Log(name, false, ex.Message);
            return false;
        }
    }

    private static async Task<bool> RunStepAsync(string name, Func<Task<StepResult>> step)
    {
        try
        {
            var result = await step();
            Log(name, result.Success, result.Message);
            return result.Success;
        }
        catch (Exception ex)
        {
            Log(name, false, ex.Message);
            return false;
        }
    }

    private static void Log(string name, bool success, string message)
    {
        var status = success ? "ok" : "fail";
        Console.WriteLine($"{name}: {status} - {message}");
    }
}

internal sealed record TvModeConfig(
    string TvIp,
    string TvMac,
    string TvInput,
    string CouchAudioMatch,
    string DeskAudioMatch,
    string CouchDisplayMatch,
    string DeskDisplayMatch,
    int? InterKeyDelayMs = null,
    string? InputMethod = null,
    int? TvInputLeftPresses = null,
    int? SourceBarOpenDelayMs = null,
    int? TvInputRightPresses = null,
    string? MinimizeDisplayMatch = null,
    bool? AssumeInputWhenOn = null,
    int? WakeSettleDelayMs = null)
{
    public TimeSpan InterKeyDelay => TimeSpan.FromMilliseconds(InterKeyDelayMs ?? 700);
    public TimeSpan SourceBarOpenDelay => TimeSpan.FromMilliseconds(SourceBarOpenDelayMs ?? 1000);
    public TimeSpan WakeSettleDelay => TimeSpan.FromMilliseconds(WakeSettleDelayMs ?? 4000);
    public string ResolvedInputMethod => string.IsNullOrWhiteSpace(InputMethod) ? "auto" : InputMethod.Trim().ToLowerInvariant();
    public bool AssumeInputWhenOnResolved => AssumeInputWhenOn ?? true;

    public bool IsValid(out string error)
    {
        var values = new[]
        {
            (nameof(TvIp), TvIp),
            (nameof(TvMac), TvMac),
            (nameof(TvInput), TvInput),
            (nameof(CouchAudioMatch), CouchAudioMatch),
            (nameof(DeskAudioMatch), DeskAudioMatch),
            (nameof(CouchDisplayMatch), CouchDisplayMatch),
            (nameof(DeskDisplayMatch), DeskDisplayMatch)
        };

        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"missing {name}";
                return false;
            }
        }

        if (!IPAddress.TryParse(TvIp, out _))
        {
            error = $"invalid tvIp: {TvIp}";
            return false;
        }

        if (!NetworkTools.TryParseMac(TvMac, out _))
        {
            error = $"invalid tvMac: {TvMac}";
            return false;
        }

        if (InterKeyDelayMs is <= 0)
        {
            error = $"invalid {nameof(InterKeyDelayMs)}: {InterKeyDelayMs}";
            return false;
        }

        if (ResolvedInputMethod is not ("auto" or "direct" or "keys"))
        {
            error = $"invalid {nameof(InputMethod)}: {InputMethod}. Expected auto, direct, or keys.";
            return false;
        }

        if (TvInputRightPresses is < 0)
        {
            error = $"invalid {nameof(TvInputRightPresses)}: {TvInputRightPresses}";
            return false;
        }

        if (TvInputLeftPresses is < 0)
        {
            error = $"invalid {nameof(TvInputLeftPresses)}: {TvInputLeftPresses}";
            return false;
        }

        if (SourceBarOpenDelayMs is < 0)
        {
            error = $"invalid {nameof(SourceBarOpenDelayMs)}: {SourceBarOpenDelayMs}";
            return false;
        }

        if (WakeSettleDelayMs is < 0)
        {
            error = $"invalid {nameof(WakeSettleDelayMs)}: {WakeSettleDelayMs}";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

internal readonly record struct StepResult(bool Success, string Message)
{
    public static StepResult Ok(string message) => new(true, message);
    public static StepResult Fail(string message) => new(false, message);
}

internal readonly record struct TvPowerStateResult(bool Reachable, string? PowerState, string Message)
{
    public bool IsOn => PowerState?.Equals("on", StringComparison.OrdinalIgnoreCase) == true;
    public string StateForLog => string.IsNullOrWhiteSpace(PowerState) ? Message : PowerState;
}

internal readonly record struct TvResolveResult(bool Success, string WorkingIp, string? Warning);

internal static class NetworkTools
{
    public static async Task<TvPowerStateResult> QueryTvPowerStateAsync(string ipAddress)
    {
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        try
        {
            using var response = await httpClient.GetAsync($"http://{ipAddress}:8001/api/v2/");
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                return new TvPowerStateResult(true, null, $"reachable, HTTP {(int)response.StatusCode}, empty device-info response");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var powerState = FindStringProperty(document.RootElement, "PowerState");
                return string.IsNullOrWhiteSpace(powerState)
                    ? new TvPowerStateResult(true, null, $"reachable, HTTP {(int)response.StatusCode}, PowerState missing")
                    : new TvPowerStateResult(true, powerState, $"reachable, HTTP {(int)response.StatusCode}, PowerState={powerState}");
            }
            catch (JsonException ex)
            {
                return new TvPowerStateResult(true, null, $"reachable, HTTP {(int)response.StatusCode}, invalid device-info JSON: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or IOException)
        {
            return new TvPowerStateResult(false, null, $"unreachable on http://{ipAddress}:8001/api/v2/: {ex.Message}");
        }
    }

    public static async Task<StepResult> WaitForPowerStateOnAsync(string ipAddress, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var seenStates = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await QueryTvPowerStateAsync(ipAddress);
            seenStates.Add(state.StateForLog);
            if (state.IsOn)
            {
                return StepResult.Ok($"PowerState sequence: {string.Join(" -> ", seenStates)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return StepResult.Fail($"timed out waiting for PowerState=on after {timeout.TotalSeconds:0}s; observed: {string.Join(" -> ", seenStates)}");
    }

    public static async Task<StepResult> SendWakeOnLanAsync(string macAddress)
    {
        if (!TryParseMac(macAddress, out var macBytes))
        {
            return StepResult.Fail($"invalid MAC address {macAddress}");
        }

        var packet = new byte[102];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 1; i <= 16; i++)
        {
            macBytes.CopyTo(packet.AsSpan(i * 6, 6));
        }

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            if (i < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        return StepResult.Ok($"sent 3 WoL packets to {NormalizeMac(macAddress)}");
    }

    public static async Task<TvResolveResult> ResolveTvIpAsync(string configuredIp, string macAddress, TimeSpan timeout)
    {
        if (await WaitForTcpAsync(configuredIp, 8002, timeout))
        {
            return new TvResolveResult(true, configuredIp, null);
        }

        var foundIp = FindIpInArpTable(macAddress);
        if (foundIp is null)
        {
            return new TvResolveResult(false, configuredIp, null);
        }

        var warning = $"TV found at {foundIp} via ARP for {NormalizeMac(macAddress)}; update tvmode.json if this is permanent";
        if (await WaitForTcpAsync(foundIp, 8002, TimeSpan.FromSeconds(5)))
        {
            return new TvResolveResult(true, foundIp, warning);
        }

        return new TvResolveResult(false, foundIp, warning + ", but port 8002 still did not respond");
    }

    public static Task<bool> CanConnectToTvRemoteAsync(string ipAddress, TimeSpan timeout)
    {
        return CanConnectAsync(ipAddress, 8002, timeout);
    }

    public static bool TryParseMac(string macAddress, out byte[] bytes)
    {
        bytes = [];
        var normalized = NormalizeMac(macAddress);
        if (normalized.Length != 12)
        {
            return false;
        }

        try
        {
            bytes = Enumerable.Range(0, 6)
                .Select(i => Convert.ToByte(normalized.Substring(i * 2, 2), 16))
                .ToArray();
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static async Task<bool> WaitForTcpAsync(string ipAddress, int port, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CanConnectAsync(ipAddress, port, TimeSpan.FromSeconds(2)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return false;
    }

    private static async Task<bool> CanConnectAsync(string ipAddress, int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(ipAddress, port, cancellation.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindIpInArpTable(string macAddress)
    {
        var normalizedMac = NormalizeMac(macAddress);
        try
        {
            var startInfo = new ProcessStartInfo("arp", "-a")
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var macMatch = Regex.Match(line, "([0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2}");
                if (!macMatch.Success || NormalizeMac(macMatch.Value) != normalizedMac)
                {
                    continue;
                }

                var ipMatch = Regex.Match(line, "(?<!\\d)(?:\\d{1,3}\\.){3}\\d{1,3}(?!\\d)");
                if (ipMatch.Success)
                {
                    return ipMatch.Value;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string NormalizeMac(string macAddress)
    {
        return new string(macAddress.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    private static string? FindStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindStringProperty(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringProperty(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }
}

internal static class SamsungTvRemote
{
    private const string AppName = "tvmode";

    public static async Task<StepResult> SwitchInputAsync(
        string ipAddress,
        string inputName,
        string tokenPath,
        string? inputMethod,
        TimeSpan interKeyDelay,
        TimeSpan sourceBarOpenDelay,
        int? leftPresses,
        int? rightPresses)
    {
        var token = ReadToken(tokenPath);
        await using var remote = new SamsungWebSocket(ipAddress, token);
        await remote.ConnectAsync();
        if (!string.IsNullOrWhiteSpace(remote.Token) && remote.Token != token)
        {
            File.WriteAllText(tokenPath, remote.Token);
        }

        remote.StartMessagePump();
        var normalizedInput = inputName.Trim().ToUpperInvariant();
        var resolvedMethod = string.IsNullOrWhiteSpace(inputMethod) ? "auto" : inputMethod.Trim().ToLowerInvariant();
        try
        {
            if (resolvedMethod == "keys")
            {
                var sequence = SamsungInputSequence.ForInput(normalizedInput, sourceBarOpenDelay, leftPresses, rightPresses);
                await remote.SendRemoteKeySequenceAsync(sequence, interKeyDelay);
                return StepResult.Ok($"sent {normalizedInput} KEY_SOURCE navigation to {ipAddress} with {interKeyDelay.TotalMilliseconds:0}ms inter-key delay");
            }

            await remote.SendDirectSourceLaunchAsync(normalizedInput);
            return StepResult.Ok($"sent direct source launch for {normalizedInput} to {ipAddress} using {resolvedMethod} input method");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"{resolvedMethod} input switch failed: {ex.Message}");
        }
    }

    public static async Task<StepResult> SendPowerKeyAsync(string ipAddress, string tokenPath)
    {
        var token = ReadToken(tokenPath);
        await using var remote = new SamsungWebSocket(ipAddress, token);
        try
        {
            await remote.ConnectAsync();
            if (!string.IsNullOrWhiteSpace(remote.Token) && remote.Token != token)
            {
                File.WriteAllText(tokenPath, remote.Token);
            }

            remote.StartMessagePump();
            await remote.SendRemoteKeyAsync("KEY_POWER");
            Console.WriteLine("wake: key -> KEY_POWER");
            return StepResult.Ok($"sent KEY_POWER to {ipAddress}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"KEY_POWER failed: {ex.Message}");
        }
    }

    public static async Task<StepResult> SendDirectInputDiagnosticAsync(string ipAddress, string inputName, string tokenPath)
    {
        var normalizedInput = inputName.Trim().ToUpperInvariant();
        var token = ReadToken(tokenPath);
        await using var remote = new SamsungWebSocket(ipAddress, token);
        await remote.ConnectAsync();
        if (!string.IsNullOrWhiteSpace(remote.Token) && remote.Token != token)
        {
            File.WriteAllText(tokenPath, remote.Token);
        }

        remote.StartMessagePump();
        await remote.SendDirectSourceLaunchAsync(normalizedInput);
        Console.WriteLine("input-direct: listening for delayed TV messages for 5 seconds");
        await Task.Delay(TimeSpan.FromSeconds(5));
        return StepResult.Ok($"sent direct source launch for {normalizedInput}");
    }

    public static async Task<StepResult> SendSingleKeyDiagnosticAsync(string ipAddress, string keyName, string tokenPath, string mode)
    {
        var normalizedKey = keyName.Trim().ToUpperInvariant();
        if (!normalizedKey.StartsWith("KEY_", StringComparison.Ordinal))
        {
            normalizedKey = "KEY_" + normalizedKey;
        }

        var normalizedMode = mode.Trim().ToUpperInvariant();
        if (normalizedMode is not ("CLICK" or "PRESSRELEASE"))
        {
            return StepResult.Fail($"unknown key diagnostic mode '{mode}', expected Click or PressRelease");
        }

        var token = ReadToken(tokenPath);
        await using var remote = new SamsungWebSocket(ipAddress, token);
        await remote.ConnectAsync();
        if (!string.IsNullOrWhiteSpace(remote.Token) && remote.Token != token)
        {
            File.WriteAllText(tokenPath, remote.Token);
        }

        remote.StartMessagePump();
        if (normalizedMode == "PRESSRELEASE")
        {
            await remote.SendRemoteKeyAsync(normalizedKey, "Press");
            Console.WriteLine($"key: sent Press {normalizedKey}");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await remote.SendRemoteKeyAsync(normalizedKey, "Release");
            Console.WriteLine($"key: sent Release {normalizedKey}");
        }
        else
        {
            await remote.SendRemoteKeyAsync(normalizedKey, "Click");
            Console.WriteLine($"key: sent Click {normalizedKey}");
        }

        Console.WriteLine("key: listening for delayed TV messages for 5 seconds");
        await Task.Delay(TimeSpan.FromSeconds(5));
        return StepResult.Ok($"sent {normalizedKey} using {normalizedMode}");
    }

    private static string? ReadToken(string tokenPath)
    {
        try
        {
            return File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class SamsungWebSocket : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly string _ipAddress;
        private readonly string? _token;
        private Task? _messagePump;

        public SamsungWebSocket(string ipAddress, string? token)
        {
            _ipAddress = ipAddress;
            _token = string.IsNullOrWhiteSpace(token) ? null : token;
            _socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        public string? Token { get; private set; }

        public async Task ConnectAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Console.WriteLine("input: ws -> opening samsung.remote.control websocket");
            await _socket.ConnectAsync(BuildUrl(), cancellation.Token);
            Console.WriteLine("input: ws -> opened, waiting for ms.channel.connect");

            while (_socket.State == WebSocketState.Open)
            {
                var response = await ReceiveTextAsync(cancellation.Token);
                if (string.IsNullOrWhiteSpace(response))
                {
                    continue;
                }

                Console.WriteLine($"input: tv <- {SummarizeMessage(response)}");
                using var document = JsonDocument.Parse(response);
                var root = document.RootElement;
                var eventName = root.TryGetProperty("event", out var eventProperty)
                    ? eventProperty.GetString()
                    : null;

                root.TryGetProperty("data", out var data);
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("token", out var tokenProperty))
                {
                    Token = tokenProperty.GetString();
                }

                if (eventName == "ms.channel.connect")
                {
                    Console.WriteLine("input: ws -> ready after ms.channel.connect");
                    return;
                }

                if (eventName == "ms.channel.unauthorized")
                {
                    throw new InvalidOperationException("TV denied websocket authorization; remove/reapprove tvmode in Device Connection Manager");
                }

                if (eventName == "ms.error")
                {
                    var message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var messageProperty)
                        ? messageProperty.GetString()
                        : response;
                    throw new InvalidOperationException(message ?? "TV websocket error");
                }
            }

            throw new InvalidOperationException("TV websocket closed before channel connect");
        }

        public Task SendDirectSourceLaunchAsync(string inputName)
        {
            var payload = new
            {
                method = "ms.channel.emit",
                @params = new
                {
                    @event = "ed.apps.launch",
                    to = "host",
                    data = new
                    {
                        appId = "org.tizen.tv.inputdevice",
                        action_type = "NATIVE_LAUNCH",
                        metaTag = inputName
                    }
                }
            };

            return SendJsonAsync(payload);
        }

        public Task SendRemoteKeyAsync(string key, string cmd = "Click")
        {
            var payload = new
            {
                method = "ms.remote.control",
                @params = new
                {
                    Cmd = cmd,
                    DataOfCmd = key,
                    Option = "false",
                    TypeOfRemote = "SendRemoteKey"
                }
            };

            return SendJsonAsync(payload);
        }

        public void StartMessagePump()
        {
            _messagePump ??= Task.Run(async () =>
            {
                while (_socket.State == WebSocketState.Open)
                {
                    try
                    {
                        var response = await ReceiveTextAsync(CancellationToken.None);
                        if (string.IsNullOrWhiteSpace(response))
                        {
                            return;
                        }

                        Console.WriteLine($"input: tv <- {SummarizeMessage(response)}");
                    }
                    catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                    {
                        Console.WriteLine($"input: tv <- receive stopped: {ex.Message}");
                        return;
                    }
                }
            });
        }

        public async Task SendRemoteKeySequenceAsync(IEnumerable<SamsungRemoteKeyStep> steps, TimeSpan interKeyDelay)
        {
            foreach (var step in steps)
            {
                for (var i = 0; i < step.Times; i++)
                {
                    await SendRemoteKeyAsync(step.Key);
                    Console.WriteLine($"input: key -> {step.Key}");
                    await Task.Delay(step.DelayAfter ?? interKeyDelay);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellation.Token);
                    if (_messagePump is not null)
                    {
                        await Task.WhenAny(_messagePump, Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token));
                    }
                }
                catch
                {
                    // Closing is best-effort because the TV can drop the socket first.
                }
            }

            _socket.Dispose();
        }

        private Uri BuildUrl()
        {
            var name = Convert.ToBase64String(Encoding.UTF8.GetBytes(AppName));
            var builder = new StringBuilder($"wss://{_ipAddress}:8002/api/v2/channels/samsung.remote.control?name={Uri.EscapeDataString(name)}");
            if (!string.IsNullOrWhiteSpace(_token))
            {
                builder.Append("&token=").Append(Uri.EscapeDataString(_token));
            }

            return new Uri(builder.ToString());
        }

        private async Task SendJsonAsync<T>(T payload)
        {
            var json = JsonSerializer.Serialize(payload, TvMode.SamsungJsonOptions);
            Console.WriteLine($"input: ws -> {json}");
            var bytes = Encoding.UTF8.GetBytes(json);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token);
        }

        private async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var memory = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return string.Empty;
                }

                memory.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(memory.ToArray());
        }

        private static string SummarizeMessage(string response)
        {
            const int maxLength = 500;
            var compact = Regex.Replace(response, "\\s+", " ").Trim();
            return compact.Length <= maxLength ? compact : compact[..maxLength] + "...";
        }
    }
}

internal static class SamsungInputSequence
{
    public static IReadOnlyList<SamsungRemoteKeyStep> ForInput(
        string inputName,
        TimeSpan sourceBarOpenDelay,
        int? configuredLeftPresses,
        int? configuredRightPresses)
    {
        var hdmiNumber = ParseHdmiNumber(inputName);
        if (hdmiNumber is null or < 1 or > 4)
        {
            throw new InvalidOperationException($"only HDMI1-HDMI4 source navigation is implemented, got {inputName}");
        }

        var leftPresses = configuredLeftPresses ?? 8;
        var rightPresses = configuredRightPresses ?? hdmiNumber.Value;
        return new[]
        {
            new SamsungRemoteKeyStep("KEY_SOURCE", 1, sourceBarOpenDelay),
            new SamsungRemoteKeyStep("KEY_LEFT", leftPresses),
            new SamsungRemoteKeyStep("KEY_RIGHT", rightPresses),
            new SamsungRemoteKeyStep("KEY_ENTER", 1)
        };
    }

    private static int? ParseHdmiNumber(string inputName)
    {
        var match = Regex.Match(inputName, "^HDMI\\s*(\\d)$", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
}

internal readonly record struct SamsungRemoteKeyStep(string Key, int Times, TimeSpan? DelayAfter = null);

internal static class DisplayManager
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;

    public static StepResult SetPrimary(string displayMatch)
    {
        var displayState = QueryActiveDisplays();
        if (displayState.Paths.Length == 0)
        {
            return StepResult.Fail("no active displays found");
        }

        var targets = displayState.Paths
            .Select((path, index) => new { Path = path, Index = index, Name = GetTargetFriendlyName(path) })
            .ToList();

        var matches = targets
            .Where(target => target.Name.Contains(displayMatch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count != 1)
        {
            var available = string.Join("; ", targets.Select(t => t.Name));
            return StepResult.Fail(matches.Count == 0
                ? $"no display matched '{displayMatch}'. Available: {available}"
                : $"multiple displays matched '{displayMatch}'. Available: {available}");
        }

        var selectedPath = matches[0].Path;
        var selectedSourceIndex = FindSourceModeIndex(displayState.Modes, selectedPath.sourceInfo.adapterId, selectedPath.sourceInfo.id);
        if (selectedSourceIndex < 0)
        {
            return StepResult.Fail($"matched display '{matches[0].Name}' has no source mode");
        }

        var selectedPosition = displayState.Modes[selectedSourceIndex].modeInfo.sourceMode.position;
        if (selectedPosition.x == 0 && selectedPosition.y == 0)
        {
            return StepResult.Ok($"{matches[0].Name} is already primary");
        }

        for (var i = 0; i < displayState.Modes.Length; i++)
        {
            if (displayState.Modes[i].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                continue;
            }

            var mode = displayState.Modes[i];
            mode.modeInfo.sourceMode.position.x -= selectedPosition.x;
            mode.modeInfo.sourceMode.position.y -= selectedPosition.y;
            displayState.Modes[i] = mode;
        }

        var setResult = NativeMethods.SetDisplayConfig(
            (uint)displayState.Paths.Length,
            displayState.Paths,
            (uint)displayState.Modes.Length,
            displayState.Modes,
            SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);

        return setResult == ERROR_SUCCESS
            ? StepResult.Ok($"set {matches[0].Name} primary")
            : StepResult.Fail($"SetDisplayConfig failed: {Win32ErrorName(setResult)} ({setResult})");
    }

    public static DisplayMonitorMatch FindMonitorDevicesByFriendlyName(string? displayMatch)
    {
        if (string.IsNullOrWhiteSpace(displayMatch))
        {
            return DisplayMonitorMatch.Skip("minimizeDisplayMatch is not configured; skipped window minimization");
        }

        var displayState = QueryActiveDisplays();
        var targets = displayState.Paths
            .Select(path => new DisplayTarget(GetTargetFriendlyName(path), GetSourceDeviceName(path)))
            .ToList();

        var matches = targets
            .Where(target => target.FriendlyName.Contains(displayMatch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            var available = string.Join("; ", targets.Select(target => $"{target.FriendlyName} ({target.DeviceName})"));
            return DisplayMonitorMatch.Skip($"no display matched minimizeDisplayMatch '{displayMatch}'. Available: {available}");
        }

        if (matches.Count > 1)
        {
            var available = string.Join("; ", matches.Select(target => $"{target.FriendlyName} ({target.DeviceName})"));
            return DisplayMonitorMatch.Skip($"multiple displays matched minimizeDisplayMatch '{displayMatch}'; skipped window minimization. Matches: {available}");
        }

        var match = matches[0];
        return DisplayMonitorMatch.Match(match.FriendlyName, match.DeviceName);
    }

    private static DisplayState QueryActiveDisplays()
    {
        var result = ERROR_SUCCESS;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = NativeMethods.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount);
            if (result != ERROR_SUCCESS)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {Win32ErrorName(result)} ({result})");
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[(int)pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[(int)modeCount];
            result = NativeMethods.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (result != ERROR_SUCCESS)
            {
                throw new InvalidOperationException($"QueryDisplayConfig failed: {Win32ErrorName(result)} ({result})");
            }

            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return new DisplayState(paths, modes);
        }

        throw new InvalidOperationException($"QueryDisplayConfig failed after retrying changed buffers: {Win32ErrorName(result)} ({result})");
    }

    private static string GetTargetFriendlyName(DISPLAYCONFIG_PATH_INFO path)
    {
        var targetName = DISPLAYCONFIG_TARGET_DEVICE_NAME.Create(
            DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
            path.targetInfo.adapterId,
            path.targetInfo.id);

        var result = NativeMethods.DisplayConfigGetDeviceInfo(ref targetName);
        if (result != ERROR_SUCCESS)
        {
            return $"target {path.targetInfo.id} ({Win32ErrorName(result)} {result})";
        }

        return string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName)
            ? $"target {path.targetInfo.id}"
            : targetName.monitorFriendlyDeviceName;
    }

    private static string GetSourceDeviceName(DISPLAYCONFIG_PATH_INFO path)
    {
        var sourceName = DISPLAYCONFIG_SOURCE_DEVICE_NAME.Create(
            DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
            path.sourceInfo.adapterId,
            path.sourceInfo.id);

        var result = NativeMethods.DisplayConfigGetDeviceInfo(ref sourceName);
        if (result != ERROR_SUCCESS)
        {
            return string.Empty;
        }

        return sourceName.viewGdiDeviceName;
    }

    private static int FindSourceModeIndex(DISPLAYCONFIG_MODE_INFO[] modes, LUID adapterId, uint sourceId)
    {
        for (var i = 0; i < modes.Length; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE &&
                modes[i].id == sourceId &&
                modes[i].adapterId.Equals(adapterId))
            {
                return i;
            }
        }

        return -1;
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

    private readonly record struct DisplayState(DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes);
    private sealed record DisplayTarget(string FriendlyName, string DeviceName);
}

internal sealed record DisplayMonitorMatch(bool ShouldMinimize, string FriendlyName, string DeviceName, string Message)
{
    public static DisplayMonitorMatch Match(string friendlyName, string deviceName)
    {
        return new DisplayMonitorMatch(true, friendlyName, deviceName, string.Empty);
    }

    public static DisplayMonitorMatch Skip(string message)
    {
        return new DisplayMonitorMatch(false, string.Empty, string.Empty, message);
    }
}

internal static class WindowManager
{
    private const int GWL_EXSTYLE = -20;
    private const int GW_OWNER = 4;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int MONITOR_DEFAULTTONULL = 0;
    private const int DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    public static StepResult MinimizeOnDisplay(string? displayMatch, string statePath)
    {
        try
        {
            DeleteStateFile(statePath);
            var display = DisplayManager.FindMonitorDevicesByFriendlyName(displayMatch);
            if (!display.ShouldMinimize)
            {
                return StepResult.Ok($"warning: {display.Message}");
            }

            var handles = new List<string>();
            var minimized = 0;
            var skipped = 0;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!ShouldConsiderWindow(hwnd))
                {
                    skipped++;
                    return true;
                }

                var monitor = NativeMethods.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
                if (monitor == IntPtr.Zero || !TryGetMonitorDeviceName(monitor, out var monitorDeviceName))
                {
                    skipped++;
                    return true;
                }

                if (monitorDeviceName.Equals(display.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.ShowWindow(hwnd, SW_MINIMIZE);
                    handles.Add(hwnd.ToInt64().ToString("X"));
                    minimized++;
                }

                return true;
            }, IntPtr.Zero);

            var state = new WindowState(handles);
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, TvMode.JsonOptions));

            return StepResult.Ok($"minimized {minimized} windows on {display.FriendlyName} ({display.DeviceName}); saved {handles.Count} handles; skipped {skipped}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail(ex.Message);
        }
    }

    public static StepResult RestoreFromState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return StepResult.Ok("no saved window state");
        }

        try
        {
            var state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(statePath), TvMode.JsonOptions);
            var restored = 0;
            var skipped = 0;
            foreach (var handleText in state?.Handles ?? [])
            {
                if (!TryParseHandle(handleText, out var hwnd) || !NativeMethods.IsWindow(hwnd))
                {
                    skipped++;
                    continue;
                }

                if (!NativeMethods.IsIconic(hwnd))
                {
                    skipped++;
                    continue;
                }

                NativeMethods.ShowWindow(hwnd, SW_RESTORE);
                restored++;
            }

            DeleteStateFile(statePath);
            return StepResult.Ok($"restored {restored} saved minimized windows; skipped {skipped}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail(ex.Message);
        }
    }

    private static bool ShouldConsiderWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        if (NativeMethods.GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (IsDwmCloaked(hwnd))
        {
            return false;
        }

        var className = GetClassName(hwnd);
        return className is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }

    private static bool IsDwmCloaked(IntPtr hwnd)
    {
        var cloaked = 0;
        var result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            DWMWA_CLOAKED,
            out cloaked,
            Marshal.SizeOf<int>());
        return result == 0 && cloaked != 0;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        var length = NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return length == 0 ? string.Empty : className.ToString();
    }

    private static bool TryGetMonitorDeviceName(IntPtr monitor, out string deviceName)
    {
        var monitorInfo = MONITORINFOEX.Create();
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            deviceName = string.Empty;
            return false;
        }

        deviceName = monitorInfo.szDevice;
        return !string.IsNullOrWhiteSpace(deviceName);
    }

    private static bool TryParseHandle(string value, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!long.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var handle))
        {
            return false;
        }

        hwnd = new IntPtr(handle);
        return hwnd != IntPtr.Zero;
    }

    private static void DeleteStateFile(string statePath)
    {
        try
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
        catch
        {
            // Stale state is not worth blocking mode switching.
        }
    }

    private sealed record WindowState(IReadOnlyList<string> Handles);
}

internal static class AudioManager
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint StgmRead = 0x00000000;
    private static readonly PROPERTYKEY PKeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PROPERTYKEY PKeyDeviceInterfaceFriendlyName = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

    public static StepResult SetDefaultPlaybackDevice(string audioMatch)
    {
        try
        {
            var devices = EnumerateActiveRenderDevices();
            var match = devices.FirstOrDefault(device => device.FriendlyName.Contains(audioMatch, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var available = devices.Count == 0 ? "(none)" : string.Join("; ", devices.Select(device => device.FriendlyName));
                return StepResult.Fail($"no audio device matched '{audioMatch}'. Available: {available}");
            }

            var policyResult = PolicyConfigFallback.SetDefaultEndpoints(match.Id);

            return policyResult.Success
                ? StepResult.Ok($"set {match.FriendlyName} as default playback device via {policyResult.InterfaceName}")
                : StepResult.Fail($"SetDefaultEndpoint failed for {match.FriendlyName}: {policyResult.Message}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail(AudioDiagnostics.FormatException("audio switch", ex));
        }
    }

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

    private sealed record AudioDevice(string Id, string FriendlyName);

    private static List<AudioDevice> EnumerateActiveRenderDevices()
    {
        return EnumerateActiveRenderDevicesForDiagnostics()
            .Select(device => new AudioDevice(device.Id, device.FriendlyName))
            .ToList();
    }
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

    [DllImport("user32.dll")]
    internal static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    internal static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

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

internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
internal readonly struct LUID : IEquatable<LUID>
{
    public readonly uint LowPart;
    public readonly int HighPart;

    public bool Equals(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    public override bool Equals(object? obj) => obj is LUID other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(LowPart, HighPart);
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

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public uint cbSize;
    public RECTL rcMonitor;
    public RECTL rcWork;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;

    public static MONITORINFOEX Create()
    {
        return new MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty
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
