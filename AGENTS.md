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

## HDR Control

For per-display Windows HDR, resolve the target through `QueryDisplayConfig` and the same friendly-name matching used by the tool's display selection. Use the matched adapter LUID and target ID with `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2` and `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE` on Windows 11. Do not use a display index or synthesize the global `Win+Alt+B` shortcut.

The legacy `advancedColorActive` bit does not mean HDR on current Windows 11; it is also true for WCG/Auto Color Management. Treat HDR as active only when the v2 `activeColorMode` is `DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR`, advanced color is active, and `highDynamicRangeUserEnabled` is true. After setting, poll and re-query until the requested v2 state is observed, and include `activeColorMode` in the result log. Never trust a successful setter return without this post-set state verification. Fall back to legacy advanced-color get/set only when v2 returns an unsupported device-info error.

Display topology updates settle asynchronously. Apply HDR only after the requested display mode is confirmed. Wait briefly before the first call and retry once after another short delay; a cold HDMI 2.1 attach needs a longer initial settle than an already-active path. Treat HDR errors as nonfatal when the tool can safely continue the rest of the transition.

## Detached Displays

An awake network device is not necessarily attached to the Windows desktop. A Samsung REST `PowerState=on` result proves that the TV is awake, but Windows may still have that display detached from the active topology. Before display-dependent couch-mode work, check `QDC_ONLY_ACTIVE_PATHS`; if the target is missing, query `QDC_ALL_PATHS | QDC_VIRTUAL_MODE_AWARE` and match the target by friendly name while retaining its adapter LUID and target ID. Deduplicate full-topology paths by that identity rather than display index.

To attach a matched available target as an extended display, copy every active path and mode unchanged, assign the new path an unused source ID, and append an explicit source mode containing the configured TV resolution and position. Relative position keywords anchor directly to the configured desk display and do not account for intervening monitors. When both explicit `x` and `y` are configured, use those absolute virtual-desktop coordinates instead. Request the configured target refresh instead of accepting best-mode defaults. For virtual-mode-aware paths, encode the source-mode index in the upper 16 bits and use an invalid clone-group ID.

Never invalidate the active paths' mode indices and ask `SetDisplayConfig` to choose modes. That shortcut can bulldoze the existing virtual-desktop layout and reset refresh rates to Windows-selected defaults. Preserve every active path's explicit mode references, supply an explicit source mode for the attached target, and omit `SDC_ALLOW_CHANGES` so Windows cannot rearrange the declared positions or modes.

Do not trust `DISPLAYCONFIG_TARGET_PREFERRED_MODE` to choose the intended refresh. TVs can advertise 60 Hz as their EDID preferred timing even when the desired and supported mode is 3840x2160 at 144 Hz; keep resolution, refresh, and coordinates as declarative configuration.

Poll until the requested resolution and physical refresh are active, verify every pre-existing display's geometry exactly, and retry once after the cold-attach settle period. Compare refresh rates with a small tolerance rather than integer equality: a nominal 144 Hz HDMI mode can negotiate as 143.857 Hz (`tvmode` uses a 0.5 Hz tolerance). Roll back to the pre-attach topology if verification fails. Desk mode should leave attachment unchanged.

HDMI audio endpoints can appear after the display path. Give couch audio discovery a short settle delay and one retry.

These display-topology, mode, and HDR lessons apply to every future display-touching tool in this monorepo, including changes to `setres`, not only to `tvmode`.

## Samsung Source Detection

The S95B/Tizen 6.5 local `http://<tv>:8001/api/v2/` response does not expose the active HDMI source. Current `samsung-tv-ws-api` and Home Assistant local integration behavior confirm that source selection is command-based; exact source reporting requires optional SmartThings cloud state.

Keep `assumeInputWhenOn` as the local-only fallback. `true` avoids disruptive source navigation by trusting an already-on TV; use `false` or `tvmode couch --force-input` when certainty matters more than preserving the current source.

## Audio Interop

On the tested Windows 11 system, MMDevice enumeration can throw `InvalidCastException` / `E_NOINTERFACE` (`0x80004002`) from `Marshal.GetTypedObjectForIUnknown`. Keep the detailed log and registry enumeration fallback; the fallback is known to work and the COM cast is a low-priority issue unless Windows removes the registry data path.
