# Building audioswap

Requires .NET 8 SDK.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output exe lands in `bin/Release/net8.0/win-x64/publish/`.

`audioswap` is built as `WinExe` so it runs windowless. It calls `AttachConsole(ATTACH_PARENT_PROCESS)` at startup so output is visible when run from an existing terminal.

Copy `audioswap.example.json` to `audioswap.json` next to the published exe. Do not commit `audioswap.json`.
