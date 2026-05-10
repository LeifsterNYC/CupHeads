# Fork notes — LeifsterNYC/CupHeads

This is a fork of [Germanized/CupHeads](https://github.com/Germanized/CupHeads). Upstream
is the real project; check there first for new releases.

Things this fork adds on top of upstream `main`:

## 1. PR #1 merged — boss health bar + battle assist timer fixes

Pulled in [Adityaki37's open PR](https://github.com/Germanized/CupHeads/pull/1) since it
hadn't been merged yet upstream:

- Boss health bar visually decreases when bosses take damage (was static)
- Boss-health overlay tracks the correct HP source for shared-health and phase-health
  fights (Frogs, Veggies, etc.)
- Battle Assist elapsed timer freezes while the pause/help screen is open
- Health bar labels derive from level/entity type (`SLIME`, `FROGS`, `FLOWER`, `VEGGIES`)
  instead of generic `LEVEL`

## 2. macOS support (`setup-mac.sh`)

Upstream ships a Windows-only Electron installer. This fork adds a shell script that
handles the equivalent setup on native macOS Cuphead:

- Locates Cuphead.app via Steam libraries (`libraryfolders.vdf` parse)
- Installs BepInEx 5.4.22 unix (the 5.4.23.5 macOS preloader has a `libc.so.6`
  `DllNotFoundException` bug — see `tasks/lessons.md` in our other repo for the writeup)
- Patches Cuphead's bundled Mono `dllmap` config with `libc.so.6 → libSystem.dylib`
  so BepInEx's preloader can find libc on macOS
- Drops the plugin DLL into `BepInEx/plugins/CupheadOnline/`
- Pre-seeds the plugin config (disables startup splash for clean testing,
  enables F11 Local Dev Lab)
- Enables BepInEx console output to the launching Terminal

Steam P2P transport works cross-platform — Windows host + macOS client is fine as long
as both are signed in to Steam and Steam friends.

### Mac install

```sh
./setup-mac.sh
# or
./setup-mac.sh "/Users/me/Library/Application Support/Steam/steamapps/common/Cuphead"
```

Then in Steam: Cuphead → Properties → Launch Options → paste the wrapper command shown
at the end of the script. Launch from Steam normally.

### Mac distribution bundle

`dist/CupHeads-mac.tar.gz` — a small tarball containing `setup-mac.sh` +
`CupheadOnline.dll` for handing to Mac users. Extract anywhere, run `./setup-mac.sh`
inside the extracted folder.

## Syncing future upstream changes

Standard fork workflow:

```sh
git remote add upstream https://github.com/Germanized/CupHeads.git
git fetch upstream main
git merge upstream/main
```

Conflicts with our PR #1 merge or Mac additions are unlikely — they touch separate code
paths (UI overlay, repo-level scripts, no plugin internals).
