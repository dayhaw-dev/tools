# Building tvmode

Requires .NET 8 SDK.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output exe lands in `bin/Release/net8.0/win-x64/publish/`.

`tvmode` is built as `WinExe` so it runs windowless. It calls `AttachConsole(ATTACH_PARENT_PROCESS)` at startup so output is visible when run from an existing terminal.

Copy `tvmode.example.json` to `tvmode.json` next to the published exe. Do not commit `tvmode.json` or `tvmode.token`.

The first `tvmode couch` run must be done while watching the Samsung TV so you can accept the on-TV permission popup. Once accepted, the returned token is persisted to `tvmode.token`.

## Samsung TV API Notes

Modern Tizen TVs use the secure websocket endpoint on port `8002`:

```text
wss://<ip>:8002/api/v2/channels/samsung.remote.control?name=<base64 app name>&token=<token>
```

The tool saves the token returned by the `ms.channel.connect` event. The tested v1 `inputMethod` is `keys`, which uses source-bar navigation based on the `samsung-tv-ws-api` command model:

```text
wait for ms.channel.connect, KEY_SOURCE, wait sourceBarOpenDelayMs, KEY_LEFT x<tvInputLeftPresses>, KEY_RIGHT x<tvInputRightPresses>, KEY_ENTER
```

The default inter-key delay is `700ms`, configurable through `interKeyDelayMs` in `tvmode.json`. The `keys` input method also supports `sourceBarOpenDelayMs`, `tvInputLeftPresses`, and `tvInputRightPresses` for tuning source-bar navigation.

Current-source reads were not found in the local 2022 Tizen REST/websocket APIs used by `samsung-tv-ws-api` or Home Assistant's built-in Samsung TV integration. `assumeInputWhenOn` defaults to `true`, so `inputMethod: "keys"` skips navigation when the TV was already reachable before wake. Run `tvmode couch --force-input` to send KEY_SOURCE navigation regardless.

`couch` minimizes windows only on `minimizeDisplayMatch`. If no display matches, it logs a warning and skips minimization. Minimized window handles are saved to `tvmode.windows.json`; `desk` restores still-valid minimized handles from that file, then deletes it.

## Audio Diagnostics

To isolate default-playback-device failures without running the full couch or desk flow:

```powershell
tvmode audio-repro "device name substring"
```

This command lists active render devices and runs only the audio switching path, including COM exception type, HRESULT, and source line where available.

## Key Diagnostics

To isolate Samsung remote key injection without running the full couch flow:

```powershell
tvmode input-direct
tvmode key KEY_HOME
tvmode key KEY_HOME PressRelease
```

`input-direct` connects to the secure Tizen websocket, waits for `ms.channel.connect`, sends only the source-launch payload for `tvInput`, logs the exact JSON sent, then listens for five seconds for delayed TV messages.

`key` sends one key payload in either `Click` or `PressRelease` mode, logs the exact JSON sent, then listens for five seconds for delayed TV messages.
