# Building tvmode

Requires .NET 8 SDK.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output exe lands in `bin/Release/net8.0/win-x64/publish/`.

`tvmode` is built as `WinExe` so it runs windowless. It calls `AttachConsole(ATTACH_PARENT_PROCESS)` at startup so output is visible when run from an existing terminal.

Copy `tvmode.example.json` to `tvmode.json` next to the published exe. Do not commit `tvmode.json` or `tvmode.token`.

The first `tvmode couch` run must be done while watching the Samsung TV so you can accept the on-TV permission popup. Once accepted, the returned token is persisted to `tvmode.token`.

See `NOTES.md` for Samsung API details, diagnostic commands, and tested S95B behavior.
