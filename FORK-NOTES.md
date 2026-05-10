# Fork notes

Fork of [Germanized/CupHeads](https://github.com/Germanized/CupHeads). Upstream is the
real project — check there for new releases.

## What's added

- **PR #1 merged** — [Adityaki37's boss-health-bar fix](https://github.com/Germanized/CupHeads/pull/1).
  Boss HP bars actually decrease during fights, label derives from the level (FROGS,
  FLOWER, etc), Battle Assist timer freezes on pause.
- **`setup-mac.sh`** — equivalent of upstream's Windows Electron installer for native
  macOS Cuphead. Auto-finds Cuphead.app via Steam libraries, installs BepInEx 5.4.22
  (5.4.23.5's macOS preloader has a libc.so.6 bug), patches the Mono dllmap, drops the
  plugin DLL, pre-seeds the config (splash off, F11 dev lab on).
- **`.gitattributes`** — LF on `.sh` so the script doesn't choke when copied Win→Mac.

## Mac install

```sh
./setup-mac.sh
```

Then in Steam: Cuphead → Properties → Launch Options → paste the wrapper command the
script prints at the end.

`dist/CupHeads-mac.tar.gz` is a pre-built bundle (`setup-mac.sh` + `CupheadOnline.dll`)
to hand someone for Mac install.

## Sync upstream

```sh
git remote add upstream https://github.com/Germanized/CupHeads.git
git fetch upstream main
git merge upstream/main
```
