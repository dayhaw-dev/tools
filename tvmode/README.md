# tvmode

One-command switching between desk mode and couch mode on Windows 11.

`tvmode couch` wakes the living room Samsung TV, selects the configured HDMI input when needed, makes the TV display primary, minimizes open windows, and switches audio to the couch device.

`tvmode desk` makes the desk display primary, restores windows that the last `couch` run minimized, and switches audio back to the desk device. It does not power off or otherwise touch the TV.

## Configuration

Copy `tvmode.example.json` to `tvmode.json` next to `tvmode.exe`, then fill in your local values:

```json
{
  "tvIp": "192.168.1.x",
  "tvMac": "AA:BB:CC:DD:EE:FF",
  "tvInput": "HDMI4",
  "couchAudioMatch": "QBQ90S",
  "deskAudioMatch": "Qudelix",
  "couchDisplayMatch": "SAMSUNG",
  "deskDisplayMatch": "LG ULTRAGEAR",
  "interKeyDelayMs": 700,
  "inputMethod": "keys",
  "tvInputLeftPresses": 5,
  "sourceBarOpenDelayMs": 1000,
  "tvInputRightPresses": 5,
  "minimizeDisplayMatch": "LG ULTRAGEAR",
  "assumeInputWhenOn": true,
  "wakeSettleDelayMs": 4000
}
```

Display and audio matches are case-insensitive substrings. `interKeyDelayMs` is optional and defaults to `700`. The tested v1 couch setup uses `inputMethod: "keys"`, `tvInputLeftPresses: 5`, `tvInputRightPresses: 5`, and `sourceBarOpenDelayMs: 1000`. If `inputMethod` is omitted it defaults to `auto`, which preserves direct-launch behavior; use `direct` to force direct source launch, or `keys` to use only source-bar navigation and never attempt direct launch. `sourceBarOpenDelayMs` waits after `KEY_SOURCE` before the first navigation key so the source-bar animation does not eat a press. `tvInputLeftPresses` controls the left-anchor burst; set it to `0` if `couch` is always run from a cold wake where the TV opens the source bar on the leftmost tuner entry, and keep the burst if `couch` may run while the TV is sitting on an arbitrary input. `tvInputRightPresses` controls how many `KEY_RIGHT` presses the `keys` method sends after the optional left-anchor burst. `wakeSettleDelayMs` defaults to `4000` and is applied after KEY_POWER or WoL wake before any input keys, giving Tizen's UI time to accept navigation. `minimizeDisplayMatch` chooses which monitor has its windows minimized during `couch`; if it is missing or does not match an active display, minimization is skipped. `assumeInputWhenOn` defaults to `true`: with `inputMethod: "keys"`, if REST reports `PowerState: "on"` before wake, `couch` assumes the input is already correct and skips source-bar navigation. Set it to `false`, or run `tvmode couch --force-input`, to force navigation. If an audio device match is not found, `tvmode` logs the available active playback device names and continues.

## Commands

```powershell
tvmode couch
tvmode couch --force-input
tvmode desk
tvmode input-direct
tvmode key KEY_HOME
tvmode key KEY_HOME PressRelease
tvmode audio-repro "Qudelix"
```

The first `couch` run will trigger a Samsung TV permission prompt. Run it while watching the TV and approve the connection. The returned websocket token is saved as `tvmode.token` next to the executable and reused on later runs.

For TV input switching, `tvmode couch` waits for `ms.channel.connect`, then uses the configured `inputMethod`. The tested S95B v1 path is `keys`: `KEY_SOURCE`, wait `sourceBarOpenDelayMs`, `tvInputLeftPresses` left-arrow presses, `tvInputRightPresses` right-arrow presses, then `KEY_ENTER`; it never attempts `ed.apps.launch`. The left-press burst navigates through the entry left of the source row as a stable anchor. The `5` left / `5` right counts were verified from PC, cable box, tuner, and cold boot. `auto` currently sends the direct Tizen source launch payload, and `direct` forces only that direct payload. All methods log the handshake, sent payloads, and any TV websocket messages seen during the sequence.

Current HDMI/source detection research for 2022 Tizen/S95B: the unauthenticated local REST device-info endpoint at `http://tvIp:8001/api/v2/` / `https://tvIp:8002/api/v2/` exposes device metadata and, on some models, `PowerState`, but not the active HDMI source. `samsung-tv-ws-api` exposes REST device info, app status/run/install, websocket app launching, and remote keys, but no current-source read. Home Assistant's built-in `samsungtv` integration similarly treats source selection as commands (`KEY_TV`, `KEY_HDMI`, or app launch) and uses device info only for power/model metadata.

Wake behavior handles the S95B's two standby depths. If REST reports `PowerState: "on"`, the TV is treated as genuinely awake and `assumeInputWhenOn` may skip navigation. If REST is reachable but `PowerState` is `standby`, missing, or ambiguous, `tvmode` treats that as fast standby: it connects to the tokenized websocket, sends `KEY_POWER`, polls REST until `PowerState: "on"` for up to about 15 seconds, waits `wakeSettleDelayMs`, then always runs KEY_SOURCE navigation. If REST is unreachable, `tvmode` treats that as deep standby/off: it sends WoL, waits for port `8002`, waits `wakeSettleDelayMs`, then runs full KEY_SOURCE navigation. `tvmode couch --force-input` overrides the on-state skip and sends KEY_SOURCE navigation regardless.

During `couch`, window minimization is scoped to `minimizeDisplayMatch`. `tvmode` enumerates visible top-level windows, skips tool/system/DWM-cloaked windows, checks each window's monitor, minimizes only windows on the matched display, and saves those window handles to `tvmode.windows.json` next to the executable. It never falls back to global minimize-all. During `desk`, `tvmode` restores only the saved handles that still exist and are still minimized, then deletes `tvmode.windows.json`. If no state file exists, restore is skipped without error.

`audio-repro` bypasses config, TV, display, and window logic. It lists active render devices, then runs only the audio switch path with detailed exception output.

`key` bypasses display, audio, and input navigation. It connects to the Samsung websocket using `tvIp` and `tvmode.token`, waits for `ms.channel.connect`, sends exactly one key, logs the exact JSON payload, then keeps listening for five seconds. The optional mode is `Click` or `PressRelease`.

`input-direct` bypasses wake, display, audio, and key navigation. It connects to the Samsung websocket using `tvIp` and `tvmode.token`, waits for `ms.channel.connect`, sends only the direct source-launch payload for `tvInput`, logs the exact JSON payload, then listens for five seconds.

## Samsung Websocket Notes

The key payload matches `samsung-tv-ws-api`'s `send_key` implementation:

```json
{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_HOME","Option":"false","TypeOfRemote":"SendRemoteKey"}}
```

That project also exposes `Cmd` values `Press` and `Release`, so `tvmode key <KEY> PressRelease` sends those as a pair for firmware that ignores `Click`.

The direct source-launch payload is also serialized with Samsung-exact casing:

```json
{"method":"ms.channel.emit","params":{"event":"ed.apps.launch","to":"host","data":{"appId":"org.tizen.tv.inputdevice","action_type":"NATIVE_LAUNCH","metaTag":"HDMI4"}}}
```

Upstream notes found while diagnosing:

- The websocket command path should not send keys until the TV emits `ms.channel.connect`.
- The default websocket port for modern secure Tizen control is `8002`.
- The command model supports `Click`, `Press`, and `Release`, with `Option` serialized as the string `"false"`.
- The library docs call out same-subnet/VLAN restrictions and Device Connection Manager authorization settings.
- An open upstream issue reports a 2026 Samsung firmware update where the remote-control channel returns `ms.channel.timeOut` while REST device info still works. That is not the same failure as a successful tokenized connect with dropped keys, but it suggests Samsung firmware can restrict or alter websocket remote behavior.

Exit code is `0` only when the display and audio steps succeed. TV wake/input and window minimization failures are logged, and the tool continues where safe.
