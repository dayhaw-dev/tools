# tvmode

One-command switching between desk mode and couch mode on Windows 11.

`tvmode couch` wakes the living room Samsung TV, selects the configured HDMI input when needed, makes the TV display primary, minimizes windows on the configured monitor, and switches audio to the couch device.

`tvmode desk` makes the desk display primary, restores windows minimized by the last `couch` run, and switches audio back to the desk device. It does not power off or otherwise touch the TV.

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
| `interKeyDelayMs` | Delay between TV remote key presses. Defaults to `700`. |
| `inputMethod` | `keys`, `direct`, or `auto`. The tested S95B path is `keys`. |
| `tvInputLeftPresses` | Left-anchor press count for source-bar navigation. |
| `sourceBarOpenDelayMs` | Delay after `KEY_SOURCE` before navigation starts. Defaults to `1000`. |
| `tvInputRightPresses` | Right press count after the left-anchor burst. |
| `minimizeDisplayMatch` | Display whose windows are minimized during `couch`. |
| `assumeInputWhenOn` | When `true`, skips key navigation if REST reports the TV is already on. Defaults to `true`. |
| `wakeSettleDelayMs` | Delay after wake before input keys are sent. Defaults to `4000`. |

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
```

## First Run

The first `couch`, `input-direct`, or `key` run will trigger a Samsung TV permission prompt. Run it while watching the TV and approve the connection. The returned websocket token is saved as `tvmode.token` next to the executable and reused later.
