# audioswap

Instantly toggles the Windows default playback device between two configured devices.

This is intended for Stream Deck or controller bindings where one button should swap between a desk DAC/headset and a TV/HDMI output.

## Configuration

Copy `audioswap.example.json` to `audioswap.json` next to `audioswap.exe`, then set the two case-insensitive device name substrings:

```json
{
  "deviceA": "Qudelix",
  "deviceB": "NVIDIA"
}
```

If the current default playback device contains `deviceA`, `audioswap` switches to the first active playback device containing `deviceB`. If the current default contains `deviceB`, it switches to `deviceA`. If the current default matches neither, it switches to `deviceA`.

If the target device is not active or not present, no change is made, the available active playback devices are printed, and the process exits `1`.

## Usage

```powershell
audioswap
```

On success, it prints:

```text
Old Device Name -> New Device Name
```
