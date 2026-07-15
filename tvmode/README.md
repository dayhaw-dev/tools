# tvmode

One-command switching between desk mode and couch mode on Windows 11.

`tvmode couch` wakes the living room Samsung TV, selects the configured HDMI input when needed, reconnects the TV to the extended Windows desktop if it was set to "Disconnect this display," makes it primary, enables HDR on the TV, minimizes windows on the configured monitor, and switches audio to the couch device.

`tvmode desk` makes the desk display primary, disables HDR on the TV, restores windows minimized by the last `couch` run, and switches audio back to the desk device. It does not power off or otherwise touch the TV.

## Configuration

Copy `tvmode.example.json` to `tvmode.json` next to `tvmode.exe`, then fill in your local values.

| Key | Description |
|-----|-------------|
| `tvIp` | Samsung TV IP address, for example `192.168.1.x`. |
| `tvMac` | Samsung TV MAC address, for example `AA:BB:CC:DD:EE:FF`. |
| `tvInput` | Target TV input, such as `HDMI4`. |
| `couchAudioMatch` | Case-insensitive substring for the couch playback device. |
| `deskAudioMatch` | Case-insensitive substring for the desk playback device. |
| `couchDisplayMatch` | Case-insensitive substring for the TV display name. |
| `deskDisplayMatch` | Case-insensitive substring for the desk display name. |
| `tvDisplayMode` | Declarative TV width, height, refresh, and placement. Defaults to 3840x2160 at 144 Hz, `rightOfDesk`. |
| `coldAttachSettleDelayMs` | Time allowed to confirm the requested mode per cold-attach attempt. Defaults to `5000`; one retry is made. |
| `interKeyDelayMs` | Delay between TV remote key presses. Defaults to `700`. |
| `inputMethod` | `keys`, `direct`, or `auto`. The tested S95B path is `keys`. |
| `tvInputLeftPresses` | Left-anchor press count for source-bar navigation. |
| `sourceBarOpenDelayMs` | Delay after `KEY_SOURCE` before navigation starts. Defaults to `1000`. |
| `tvInputRightPresses` | Right press count after the left-anchor burst. |
| `minimizeDisplayMatch` | Display whose windows are minimized during `couch`. |
| `assumeInputWhenOn` | Local REST cannot identify the active HDMI. `true` trusts an already-on TV; use `false` or `couch --force-input` when it may be on another source. Defaults to `true`. |
| `wakeSettleDelayMs` | Delay after wake before input keys are sent. Defaults to `4000`. |
| `hdr` | `on` enables per-display HDR switching; `off` skips HDR changes. Defaults to `on`. |

`tvDisplayMode.position` accepts `rightOfDesk`, `leftOfDesk`, `aboveDesk`, or `belowDesk`; each places the TV directly against that edge of `deskDisplayMatch` and does not route around another monitor. For a non-adjacent TV, omit `position` and set both `x` and `y` to its absolute virtual-desktop coordinates. Coordinates take precedence if `position` is also present. Force-attach preserves every active display's existing size and position, requests the configured TV mode explicitly, and rolls back if either the mode or layout cannot be confirmed.

HDR is changed only on the display matched by `couchDisplayMatch`. On Windows 11, state is read with `GET_ADVANCED_COLOR_INFO_2`, changed with `SET_HDR_STATE`, and re-read until `activeColorMode` confirms HDR or non-HDR state; legacy advanced-color APIs are fallback only. After a cold attach or mode correction, HDR waits an additional three seconds following the primary-display change. HDR errors are logged as warnings and do not stop the rest of the transition.

After attaching the TV, audio discovery waits briefly and retries once so the HDMI playback endpoint has time to appear. `desk` leaves the TV's attached/detached state unchanged.

See [implementation notes](NOTES.md) for Samsung API details, diagnostics, and the tested S95B input behavior.

## Commands

```powershell
tvmode couch
tvmode couch --force-input
tvmode desk
tvmode input-direct
tvmode key KEY_HOME
tvmode key KEY_HOME PressRelease
tvmode audio-repro "Qudelix"
tvmode displays
tvmode hdr-status "QBQ90S"
```

## First Run

The first `couch`, `input-direct`, or `key` run will trigger a Samsung TV permission prompt. Run it while watching the TV and approve the connection. The returned websocket token is saved as `tvmode.token` next to the executable and reused later.
