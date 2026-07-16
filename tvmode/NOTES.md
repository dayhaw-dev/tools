# tvmode Implementation Notes

These notes keep the diagnostic history and Samsung TV behavior details out of the short user-facing README.

## Configuration Behavior

Display and audio matches are case-insensitive substrings.

The tested S95B couch setup uses `inputMethod: "keys"`, `tvInputLeftPresses: 5`, `tvInputRightPresses: 5`, `sourceBarOpenDelayMs: 1000`, `coldSourceBarOpenDelayMs: 3000`, and `wakeSettleDelayMs: 4000`.

If `inputMethod` is omitted it defaults to `auto`, which preserves direct-launch behavior. Use `direct` to force direct source launch, or `keys` to use only source-bar navigation and never attempt direct launch.

`sourceBarOpenDelayMs` waits after `KEY_SOURCE` before the first navigation key so the source-bar animation does not eat a press.

`coldSourceBarOpenDelayMs` defaults to `3000` and replaces `sourceBarOpenDelayMs` only when initial REST contact failed and the run took the deep-standby/WoL path. The TV can accept `KEY_SOURCE` before its freshly booted source bar is ready for directional keys; the longer delay prevents the left-anchor burst from being partially eaten. Already-on and fast-standby/KEY_POWER paths keep the shorter warm delay.

`tvInputLeftPresses` controls the left-anchor burst. Set it to `0` if `couch` is always run from a cold wake where the TV opens the source bar on the leftmost tuner entry, and keep the burst if `couch` may run while the TV is sitting on an arbitrary input.

`tvInputRightPresses` controls how many `KEY_RIGHT` presses the `keys` method sends after the optional left-anchor burst.

`wakeSettleDelayMs` defaults to `4000` and is applied after KEY_POWER or WoL wake before any input keys, giving Tizen's UI time to accept navigation.

`minimizeDisplayMatch` chooses which monitor has its windows minimized during `couch`; if it is missing or does not match an active display, minimization is skipped.

`assumeInputWhenOn` defaults to `true`. With `inputMethod: "keys"`, if REST reports `PowerState: "on"` before wake, `couch` assumes the input is already correct and skips source-bar navigation. Set it to `false`, or run `tvmode couch --force-input`, to force navigation.

If an audio device match is not found, `tvmode` logs the available active playback device names and continues.

Exit code is `0` only when the display and audio steps succeed. TV wake/input and window minimization failures are logged, and the tool continues where safe.

## Samsung Websocket

Modern Tizen TVs use the secure websocket endpoint on port `8002`:

```text
wss://<ip>:8002/api/v2/channels/samsung.remote.control?name=<base64 app name>&token=<token>
```

The tool saves the token returned by the `ms.channel.connect` event. The first run must be done while watching the Samsung TV so you can accept the on-TV permission popup. Once accepted, the returned token is persisted to `tvmode.token`.

For TV input switching, `tvmode couch` waits for `ms.channel.connect`, then uses the configured `inputMethod`. The tested S95B path is `keys`:

```text
wait for ms.channel.connect, KEY_SOURCE, wait sourceBarOpenDelayMs, KEY_LEFT x<tvInputLeftPresses>, KEY_RIGHT x<tvInputRightPresses>, KEY_ENTER
```

The left-press burst navigates through the entry left of the source row as a stable anchor. The `5` left / `5` right counts were verified from PC, cable box, tuner, and cold boot.

`auto` currently sends the direct Tizen source launch payload, and `direct` forces only that direct payload. The S95B v1 tested path uses `keys` and never attempts `ed.apps.launch`.

All methods log the handshake, sent payloads, and any TV websocket messages seen during the sequence.

## Payloads

The key payload matches `samsung-tv-ws-api`'s `send_key` implementation:

```json
{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_HOME","Option":"false","TypeOfRemote":"SendRemoteKey"}}
```

That project also exposes `Cmd` values `Press` and `Release`, so `tvmode key <KEY> PressRelease` sends those as a pair for firmware that ignores `Click`.

The direct source-launch payload is also serialized with Samsung-exact casing:

```json
{"method":"ms.channel.emit","params":{"event":"ed.apps.launch","to":"host","data":{"appId":"org.tizen.tv.inputdevice","action_type":"NATIVE_LAUNCH","metaTag":"HDMI4"}}}
```

That payload remains available only for diagnostics and explicit `direct`/`auto` configuration. It is not a supported direct-input route on the tested S95B: the TV accepts it without changing source or returning a useful result.

## Source Detection Research

Current HDMI/source detection research for 2022 Tizen/S95B: the unauthenticated local REST device-info endpoint at `http://tvIp:8001/api/v2/` / `https://tvIp:8002/api/v2/` exposes device metadata and, on some models, `PowerState`, but not the active HDMI source.

`samsung-tv-ws-api` exposes REST device info, app status/run/install, websocket app launching, and remote keys, but no current-source read.

The current [`samsung-tv-ws-api` remote implementation](https://github.com/xchwarze/samsung-tv-ws-api/blob/master/samsungtvws/remote.py) launches installed apps through `ed.apps.launch`, but it has no HDMI app identifier or source-selection method. Its dedicated [HDMI-selection issue](https://github.com/xchwarze/samsung-tv-ws-api/issues/112) concluded that explicit HDMI selection is unavailable locally; `KEY_HDMI` can only cycle inputs from a known starting state.

Home Assistant's built-in [`samsungtv` integration](https://github.com/home-assistant/core/blob/dev/homeassistant/components/samsungtv/media_player.py) likewise maps only `TV` and generic `HDMI` to `KEY_TV` and `KEY_HDMI`; installed applications are the only sources launched by app ID. Its [specific-HDMI investigation](https://github.com/home-assistant/core/issues/147250) found that adding `KEY_HDMI1`, `KEY_HDMI2`, and similar names made options visible but the TVs silently ignored those keys. Direct port selection reported by other integrations uses SmartThings cloud state/control rather than the local port 8002 channel.

Integrations that report an exact HDMI source on these models rely on optional SmartThings cloud state. `tvmode` remains local-only and does not require a Samsung account or cloud token.

For this S95B path, `tvmode` therefore uses the fallback policy: if REST reports `PowerState: "on"` before wake and `assumeInputWhenOn` is true, key navigation may be skipped; standby/off wake paths always run KEY_SOURCE navigation.

## Wake Behavior

Wake behavior handles the S95B's two standby depths.

If REST reports `PowerState: "on"`, the TV is treated as genuinely awake and `assumeInputWhenOn` may skip navigation.

If REST is reachable but `PowerState` is `standby`, missing, or ambiguous, `tvmode` treats that as fast standby: it connects to the tokenized websocket, sends `KEY_POWER`, polls REST until `PowerState: "on"` for up to about 15 seconds, waits `wakeSettleDelayMs`, then always runs KEY_SOURCE navigation.

If REST is unreachable, `tvmode` treats that as deep standby/off: it sends WoL, waits for port `8002`, waits `wakeSettleDelayMs`, then runs full KEY_SOURCE navigation.

On that deep-standby path, KEY_SOURCE navigation uses `coldSourceBarOpenDelayMs` after opening the source bar. Other navigation paths use `sourceBarOpenDelayMs`.

`tvmode couch --force-input` overrides the on-state skip and sends KEY_SOURCE navigation regardless.

## Windows

Before the `couch` primary-display step, `tvmode` checks the active display paths for `couchDisplayMatch`. If the TV is absent, it queries `QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE`, deduplicates available targets by adapter LUID and target ID, and selects the same friendly-name match used by primary-display switching.

The force-attach design uses a persisted declarative mode rather than the display's EDID preferred mode. `DISPLAYCONFIG_TARGET_PREFERRED_MODE` describes the monitor's best EDID mode but does not preserve desktop position and does not guarantee the S95B's 144 Hz PC/game-mode timing. `tvDisplayMode` therefore defaults to 3840x2160 at 144 Hz and positions the TV edge-to-edge relative to `deskDisplayMatch`.

The relative `position` values are literal anchors to the desk display; they do not search around intervening monitors. Set both `tvDisplayMode.x` and `tvDisplayMode.y` for a non-adjacent TV. These are absolute `DISPLAYCONFIG_SOURCE_MODE.position` coordinates and take precedence over `position`. A partial coordinate pair is rejected during config validation.

The attach request copies the current active paths and modes unchanged, appends one explicit TV `DISPLAYCONFIG_SOURCE_MODE`, and requests the configured target refresh. For virtual-mode-aware paths, the source mode index is encoded in the upper 16 bits with an invalid clone group, keeping the path extended. `SDC_ALLOW_CHANGES` is deliberately omitted so Windows cannot rearrange supplied source geometry. `SDC_FORCE_MODE_ENUMERATION` gives the HDMI driver an opportunity to refresh its mode list.

After each apply, `tvmode` polls for up to `coldAttachSettleDelayMs` and verifies the TV's resolution, physical refresh, and expected position, plus the size and position of every display that was active before the attach. It reapplies once if HDMI 2.1 negotiation is still settling. If verification still fails, it restores the exact pre-attach topology and skips HDR because the configured TV mode was not confirmed. `desk` never disables or detaches the TV.

During `couch`, window minimization is scoped to `minimizeDisplayMatch`.

`tvmode` enumerates visible top-level windows, skips tool/system/DWM-cloaked windows, checks each window's monitor, minimizes only windows on the matched display, and saves those window handles to `tvmode.windows.json` next to the executable. It never falls back to global minimize-all.

During `desk`, `tvmode` restores only the saved handles that still exist and are still minimized, then deletes `tvmode.windows.json`. If no state file exists, restore is skipped without error.

### HDR

HDR uses the Windows display configuration API, never the global `Win+Alt+B` shortcut. The TV is resolved from `couchDisplayMatch` through the same active-path friendly-name lookup used for primary-display switching; the resulting adapter LUID and target ID are used for every query and set.

On Windows 11, `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2` is authoritative. The legacy `advancedColorActive` bit can be true for SDR wide-color-gamut/Auto Color Management, so it must not be interpreted as HDR. HDR is on only when `activeColorMode` is `DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR`, `advancedColorActive` is true, and `highDynamicRangeUserEnabled` is true.

HDR enable changes use `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE`. After a successful setter call, `tvmode` polls the v2 query for up to three seconds and logs the resulting `activeColorMode`; accepting the setter is not success by itself. Device-info types 15/16 are unavailable before Windows 11, so only `ERROR_INVALID_FUNCTION`, `ERROR_NOT_SUPPORTED`, or `ERROR_INVALID_PARAMETER` trigger the legacy `GET_ADVANCED_COLOR_INFO` / `SET_ADVANCED_COLOR_STATE` fallback.

The couch operation normally waits one second after the primary-display operation and retries once after another one-second delay because display topology changes can leave the HDR API temporarily unavailable. A successful cold attach or mode correction uses a three-second initial HDR settle after the configured mode has already been confirmed. HDR failures are warnings and do not affect the transition exit code. Set `hdr` to `off` to skip couch HDR enablement; the default is `on`. The desk path does not query, enable, disable, or verify HDR.

### Audio endpoint timing

The couch audio switch waits one second after the display work, tries the configured endpoint, and retries once after another one-second delay. This covers the delay between attaching an HDMI display path and Windows publishing its render endpoint.

On the tested system, MMDevice enumeration throws `InvalidCastException` (`E_NOINTERFACE`, `0x80004002`) in `Marshal.GetTypedObjectForIUnknown`. This is a known low-priority interop issue: `tvmode` logs the exception and falls back to active endpoint enumeration under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render`, which works on that system.

### Hardware verification

The v1.2.1 couch and desk transitions were verified end to end on the S95B setup. Starting with the TV manually detached, `couch` restored the configured virtual-desktop coordinates without moving the existing displays, established 3840x2160 at 144 Hz, enabled HDR with `activeColorMode=HDR`, and found the HDMI audio endpoint on the first attempt. `desk` restored the desk primary display, saved windows, and desk audio, then disabled HDR with the non-HDR active color mode confirmed by the v2 query.

## Diagnostics

Every run, including diagnostics and invalid command/config paths, is timestamped in `tvmode.log` next to the executable. Both stdout and stderr are captured. The file rotates at 1 MiB with three retained backups, and each run records its tool version, command, process ID, elapsed time, and exit code. Concurrent processes open the log with shared read/write access and serialize each seek-and-append through a path-scoped named mutex; every line carries its PID so overlapping traces remain attributable. Rotation is deferred while another run still has the current log open.

`tvmode --version` prints only the tool name and semantic version to the terminal and returns exit code `0`; its versioned run header and footer are still written to the rolling log.

`displays` lists each active display's friendly name, DisplayConfig source position, source resolution, physical refresh, and primary state without changing topology. Use these coordinates for `tvDisplayMode.x` and `y`; unlike `System.Windows.Forms.Screen.Bounds`, they are not DPI-virtualized.

```powershell
tvmode displays
```

`hdr-status` performs the same v2-first state query used after HDR changes and reports `activeColorMode`, the HDR user-enabled/support bits, advanced-color activity, and bit depth without changing state.

```powershell
tvmode hdr-status "display name substring"
```

`audio-repro` bypasses config, TV, display, and window logic. It lists active render devices, then runs only the audio switch path with detailed exception output, including COM exception type, HRESULT, and source line where available.

```powershell
tvmode audio-repro "device name substring"
```

`key` bypasses display, audio, and input navigation. It connects to the Samsung websocket using `tvIp` and `tvmode.token`, waits for `ms.channel.connect`, sends exactly one key, logs the exact JSON payload, then keeps listening for five seconds. The optional mode is `Click` or `PressRelease`.

```powershell
tvmode key KEY_HOME
tvmode key KEY_HOME PressRelease
```

`input-direct` bypasses wake, display, audio, and key navigation. It connects to the Samsung websocket using `tvIp` and `tvmode.token`, waits for `ms.channel.connect`, sends only the direct source-launch payload for `tvInput`, logs the exact JSON sent, then listens for five seconds for delayed TV messages.

```powershell
tvmode input-direct
```

## Upstream Notes

- The websocket command path should not send keys until the TV emits `ms.channel.connect`.
- The default websocket port for modern secure Tizen control is `8002`.
- The command model supports `Click`, `Press`, and `Release`, with `Option` serialized as the string `"false"`.
- The library docs call out same-subnet/VLAN restrictions and Device Connection Manager authorization settings.
- An open upstream issue reports a 2026 Samsung firmware update where the remote-control channel returns `ms.channel.timeOut` while REST device info still works. That is not the same failure as a successful tokenized connect with dropped keys, but it suggests Samsung firmware can restrict or alter websocket remote behavior.
