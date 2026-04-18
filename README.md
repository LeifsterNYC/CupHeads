# CupHeads

Steam P2P multiplayer for Cuphead, plus a desktop installer that handles the mod setup for you.

## What this repo contains

- `CupheadOnline/` - the BepInEx + Harmony mod
- `CupheadInstaller/` - the Electron installer app
- `build.ps1` - root build script that builds the mod and packages the Electron installer

## Features

### Multiplayer

- Steam P2P transport built for Cuphead through BepInEx + Harmony
- Host, join, invite friend, retry last action, and copy lobby diagnostics
- Host-led save slot flow so the host can open a file and pull the guest into the same run
- Host-authoritative scene syncing for level and menu transitions
- Save compatibility checks with warnings for mismatched progress or setup
- Live connection HUD with role, status, session info, and sync warnings
- In-game session panel with `F8` toggle for quick diagnostics and session state
- Optional boss HP scaling per extra active player, configurable in BepInEx and disabled by default
- Recovery and resync tools for active sessions, including exported bug-report bundles

### Menu and UX

- Custom multiplayer menu injected into Slot Select
- Dedicated credits screen
- Clear Steam readiness and connection status messaging
- Clipboard helpers for lobby IDs and diagnostics
- Better retry and reconnect guidance when Steam sessions fail

### Characters and DLC

- Cuphead and Mugman are both supported in the main co-op flow
- Ms. Chalice is supported when the Delicious Last Course DLC is installed and her charm/loadout is available
- Save compatibility checks look at DLC world access and DLC progression before the host starts a run

## Current Gameplay Limit

- Active gameplay is still capped by Cuphead's native two-player runtime, so live runs remain `PlayerOne` + `PlayerTwo`
- The current mod architecture supports richer lobby/session flow, but it does not turn Cuphead into a true unlimited-player game

### Installer

- Automatic Cuphead detection through Steam
- Automatic BepInEx repair or installation when needed
- Repair-style installs that always refresh the bundled `CupheadOnline.dll`
- Automatic cleanup of obsolete `LiteNetLib.dll` leftovers from older builds
- Final verification after install so the setup is checked before you launch
- One-click mod installation
- Portable desktop installer
- Built-in verify, open-folder, and launch-Steam helpers

## Requirements

- Windows 10 or later
- Cuphead installed through Steam
- Internet connection for BepInEx download during install

## Quick Start

1. Download `Cupheads.exe` from [Releases](https://github.com/Germanized/CupHeads/releases).
2. Run the installer.
3. Let it detect your Cuphead folder, or browse to it manually.
4. Click Install.
5. Launch Cuphead through Steam.

If you test outside the Steam launcher, you may need a `steam_appid.txt` next to `Cuphead.exe`.

## How installation works

The Electron installer:

- detects your Cuphead install
- repairs or installs BepInEx 5 if it is missing or damaged
- always refreshes `CupheadOnline.dll` in `Cuphead\\BepInEx\\plugins\\CupheadOnline\\`
- removes stale `LiteNetLib.dll` leftovers from the older UDP transport
- verifies the final install before finishing

The mod then patches the game's menu and gameplay flow through Harmony and uses Steamworks P2P for multiplayer.

## Building from source

### Build the mod only

```powershell
dotnet build .\CupheadOnline\CupheadOnline.csproj -c Release
```

### Build the full release package

```powershell
.\build.ps1 -Release
```

That produces:

- `dist\CupheadOnline.dll`
- `dist\Cupheads.exe`

### Build the installer manually

```powershell
cd .\CupheadInstaller
npm install
npm run dist
```

The packaged installer is written to `CupheadInstaller\dist\Cupheads.exe`.

## Tech stack

- BepInEx 5
- Harmony 2
- Steamworks P2P
- Electron + Node.js

## Credits

- Germanized and Sh0kr for the mod
- Made for Daniel
- BepInEx for the mod framework
- Harmony for patching
- Electron for the installer shell
