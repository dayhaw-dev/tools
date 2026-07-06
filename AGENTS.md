# Repository Guidelines

## Repository Layout

This is a public tools monorepo. Each tool lives in its own top-level folder and follows the same small-project shape:

- `Program.cs` for the tool implementation.
- `<tool>.csproj` for the .NET project file.
- `README.md` for user-facing usage notes.
- `BUILD.md` for build and release instructions.

Use `setres/` as the reference pattern for new tools and for keeping existing tools consistent.

## Tool Project Conventions

Tools target `.NET 8` with `TargetFramework` set to `net8.0`.

Windows command-line tools should use `OutputType` `WinExe` so they run windowless. Because `WinExe` does not automatically attach to the invoking terminal, call `AttachConsole(ATTACH_PARENT_PROCESS)` before writing terminal output. If needed, call `FreeConsole()` after the initial attach and reattach before printing, matching the `setres/Program.cs` pattern.

Published tools should be Windows x64, self-contained, single-file executables. The project file should include the relevant properties, following `setres/setres.csproj`:

- `RuntimeIdentifier` `win-x64`
- `SelfContained` `true`
- `PublishSingleFile` `true`
- `EnableCompressionInSingleFile` `true`

Enable nullable reference types and implicit usings unless a specific tool has a reason not to.

## Build And Release

Build tools with the .NET 8 SDK. The standard publish command is:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The published executable lands under `bin/Release/net8.0/win-x64/publish/` inside the tool folder.

Do not commit compiled outputs. Binaries are published to GitHub Releases manually.

## Git Hygiene

Never commit:

- compiled `.exe` files
- `bin/`
- `obj/`
- other generated build outputs

Keep changes scoped to the relevant tool folder unless the repository-level documentation or shared conventions need updating.
