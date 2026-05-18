# GPD UI Navigator

Native Windows helper for fast joystick-driven UI navigation in GPD mouse mode.

## Controls

- `Ctrl+Alt+F12`: toggle enabled.
- Hold the activation key, default `F13`: enter navigation mode.
- Move the right stick while held: select the next UI target in that direction.
- Release the activation key: move to the selected target and click it.
- `Scan Active Window`: list UI Automation targets for the current foreground window.

## Current Integration Strategy

The app uses:

- A low-level keyboard hook for the held activation key.
- Raw Input mouse deltas for joystick direction in GPD mouse mode.
- Windows UI Automation for visible target discovery.
- A click-through overlay for large target hints.
- Win32 `SetCursorPos` / `SendInput` for final click and keyboard fallback actions.

In GPD mouse mode, Windows does not expose `L2` as a held controller button. Remap `L2` to the configured activation key (`F13` by default) in the GPD/remapping layer, then this app handles target selection and clicking.

Settings are stored in `%LOCALAPPDATA%\GpdUiSnap\settings.json`.

## Download

Download the latest release from the repository's Releases page, then run `GpdUiSnap.exe`.

If you build from source, install the .NET 8 SDK first.

## Build

```powershell
dotnet build .\GpdUiSnap.csproj
```

## Run

```powershell
dotnet run --project .\GpdUiSnap.csproj
```
