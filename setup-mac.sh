#!/usr/bin/env bash
# CupHeads (Germanized fork) installer for native macOS Cuphead.
#
# The upstream Windows installer (CupheadInstaller/, an Electron app) is Windows-only.
# This shell script does the equivalent setup on macOS:
#   1. Locates Cuphead.app via Steam libraries
#   2. Installs BepInEx 5.4.22 (with macOS Mono dllmap fix for libc.so.6)
#   3. Drops CupheadOnline.dll into BepInEx/plugins/CupheadOnline/
#   4. Pre-seeds com.cupheadonline.mod.cfg (disables startup splash for clean testing)
#
# Steam P2P transport is used by the mod — works cross-platform between Windows host
# and macOS client as long as both PCs are signed into Steam and friends.
#
# Usage:
#   ./setup-mac.sh [path-to-Cuphead-folder]
#
# Examples:
#   ./setup-mac.sh
#   ./setup-mac.sh "/Users/me/Library/Application Support/Steam/steamapps/common/Cuphead"
#
# Pre-reqs:
#   - Cuphead installed via Steam (native Mac build).
#   - This script lives in the same folder as CupheadOnline.dll (or a CupheadOnline/
#     folder containing the DLL, or a CupHeads-mac-vX.Y.Z.tar.gz bundle).
#   - bash, curl, ditto, unzip (all preinstalled on macOS).

set -euo pipefail

CUPHEAD_DIR="${1:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# BepInEx 5.4.23.5 macOS_universal ships a Preloader.dll that crashes on launch with
# `DllNotFoundException: libc.so.6` — its platform detection unconditionally tries the
# Linux uname before catching, and on macOS the call hard-fails before the OSX fallback
# fires. 5.4.22's unix preloader catches the missing-libc and falls through cleanly.
# Don't bump this URL without confirming the upstream macOS preloader is fixed.
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_unix_5.4.22.0.zip"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Locate the plugin payload. Accept any of:
#   (a) CupheadOnline.dll sitting next to the script,
#   (b) CupheadOnline/CupheadOnline.dll (folder layout),
#   (c) CupHeads-mac-vX.Y.Z.tar.gz wrapper (extracts to either of the above).
PLUGIN_DLL=""
if [[ -f "$SCRIPT_DIR/CupheadOnline.dll" ]]; then
  PLUGIN_DLL="$SCRIPT_DIR/CupheadOnline.dll"
elif [[ -f "$SCRIPT_DIR/CupheadOnline/CupheadOnline.dll" ]]; then
  PLUGIN_DLL="$SCRIPT_DIR/CupheadOnline/CupheadOnline.dll"
else
  for tarball in "$SCRIPT_DIR"/CupHeads-mac-v*.tar.gz "$SCRIPT_DIR"/CupHeads-mac.tar.gz; do
    if [[ -f "$tarball" ]]; then
      tar -xzf "$tarball" -C "$TMP/"
      for try in "$TMP"/CupHeads-mac/CupheadOnline.dll \
                 "$TMP"/CupHeads-mac/CupheadOnline/CupheadOnline.dll \
                 "$TMP"/CupheadOnline.dll \
                 "$TMP"/CupheadOnline/CupheadOnline.dll; do
        if [[ -f "$try" ]]; then PLUGIN_DLL="$try"; break; fi
      done
      [[ -n "$PLUGIN_DLL" ]] && break
    fi
  done
fi

if [[ -z "$PLUGIN_DLL" ]]; then
  echo "error: couldn't find CupheadOnline.dll next to this script." >&2
  echo "       expected one of:" >&2
  echo "         $SCRIPT_DIR/CupheadOnline.dll" >&2
  echo "         $SCRIPT_DIR/CupheadOnline/CupheadOnline.dll" >&2
  echo "         $SCRIPT_DIR/CupHeads-mac-vX.Y.Z.tar.gz" >&2
  exit 1
fi
echo "✓ Plugin DLL: $PLUGIN_DLL"

# 1. Locate Cuphead.
if [[ -z "$CUPHEAD_DIR" ]]; then
  for candidate in \
    "$HOME/Library/Application Support/Steam/steamapps/common/Cuphead" \
    "/Applications/Steam/steamapps/common/Cuphead"; do
    if [[ -d "$candidate" ]]; then
      CUPHEAD_DIR="$candidate"
      break
    fi
  done

  # Fallback: scan all Steam libraries listed in libraryfolders.vdf.
  if [[ -z "$CUPHEAD_DIR" ]]; then
    LIB_VDF="$HOME/Library/Application Support/Steam/steamapps/libraryfolders.vdf"
    if [[ -f "$LIB_VDF" ]]; then
      while IFS= read -r path; do
        local_path="${path}/steamapps/common/Cuphead"
        if [[ -d "$local_path" ]]; then
          CUPHEAD_DIR="$local_path"
          break
        fi
      done < <(grep -oE '"path"[[:space:]]*"[^"]*"' "$LIB_VDF" | sed -E 's/.*"path"[[:space:]]*"([^"]*)".*/\1/')
    fi
  fi
fi

if [[ -z "$CUPHEAD_DIR" || ! -d "$CUPHEAD_DIR" ]]; then
  echo "error: couldn't auto-find Cuphead. Pass the install folder as an arg." >&2
  echo "       e.g. $0 \"$HOME/Library/Application Support/Steam/steamapps/common/Cuphead\"" >&2
  exit 2
fi

# Sanity-check we're in the right place.
APP=$(find "$CUPHEAD_DIR" -maxdepth 2 -name "Cuphead.app" -type d -print -quit 2>/dev/null || true)
if [[ -z "$APP" ]]; then
  echo "error: Cuphead.app not found inside $CUPHEAD_DIR" >&2
  echo "       (this script targets the native macOS build; Wine/Whisky needs the Windows installer.)" >&2
  exit 3
fi

echo "✓ Cuphead at: $CUPHEAD_DIR"
echo "  app:        $APP"

# 2. Install BepInEx if missing.
if [[ ! -f "$CUPHEAD_DIR/run_bepinex.sh" || ! -d "$CUPHEAD_DIR/BepInEx/core" ]]; then
  echo "→ Installing BepInEx 5.4.22 (unix)…"
  curl -L --fail --progress-bar -o "$TMP/bep.zip" "$BEPINEX_URL"
  ditto -x -k "$TMP/bep.zip" "$TMP/bep"
  /bin/cp -R "$TMP/bep/" "$CUPHEAD_DIR/"
  chmod +x "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  # 5.4.22's run_bepinex.sh ships with a blank executable_name; on macOS this must
  # point at the .app bundle so doorstop knows what to inject into.
  /usr/bin/sed -i '' 's/^executable_name=""$/executable_name="Cuphead.app"/' "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR/run_bepinex.sh" "$CUPHEAD_DIR/BepInEx" 2>/dev/null || true
  echo "✓ BepInEx installed"
else
  echo "✓ BepInEx already present"
fi

# 2a. Patch Cuphead's bundled Mono dllmap so BepInEx's preloader can find libc.
# BepInEx 5.x's PlatformUtils hard-codes [DllImport("libc.so.6")] for Linux uname()
# detection. On macOS Mono's OSVersion.Platform reports "Unix", so the preloader takes
# the Linux branch and crashes with DllNotFoundException before any plugin loads.
# Fix: add a libc.so.6 → libSystem.dylib remap to the dllmap config Mono reads at startup.
# Idempotent — adds one line if missing.
for cfg in \
  "$CUPHEAD_DIR/Cuphead.app/Contents/Mono/etc/mono/config" \
  "$CUPHEAD_DIR/Cuphead.app/Contents/MonoBleedingEdge/etc/mono/config"; do
  if [[ -f "$cfg" ]] && ! grep -q 'libc.so.6' "$cfg"; then
    /usr/bin/sed -i '' \
      -e '/<dllmap dll="libc" target="libc.dylib"/a\
\	<dllmap dll="libc.so.6" target="/usr/lib/libSystem.dylib" os="osx"/>' "$cfg"
    echo "✓ Mono dllmap patched: $cfg"
  fi
done

# 3. Drop the plugin DLL.
PLUGIN_DEST="$CUPHEAD_DIR/BepInEx/plugins/CupheadOnline"
mkdir -p "$PLUGIN_DEST"
/bin/cp -f "$PLUGIN_DLL" "$PLUGIN_DEST/CupheadOnline.dll"
xattr -dr com.apple.quarantine "$PLUGIN_DEST" 2>/dev/null || true
echo "✓ Plugin DLL: $PLUGIN_DEST/CupheadOnline.dll"

# 4. Pre-seed CupheadOnline config with sensible defaults for the testing path.
# Notably: disable the startup splash video — a 1080p H.264 intro plays on first
# launch by default and adds friction for testers.
mkdir -p "$CUPHEAD_DIR/BepInEx/config"
cat > "$CUPHEAD_DIR/BepInEx/config/com.cupheadonline.mod.cfg" <<'EOF'
## Settings file pre-seeded by setup-mac.sh.

[Debug]
EnableLocalDevSessionHotkey = true
VerboseLogging = false

[StartupSplash]
EnableStartupSplash = false

[Networking]
LatencyFriendlyDamage = true
EOF
echo "✓ Plugin config pre-seeded (splash disabled, dev-lab F11 enabled)"

# 5. Enable BepInEx console — uses the launching Terminal as console.
BEP_CFG="$CUPHEAD_DIR/BepInEx/config/BepInEx.cfg"
if [[ -f "$BEP_CFG" ]]; then
  /usr/bin/sed -i '' '/^\[Logging\.Console\]/,/^\[/ { s/^Enabled = false$/Enabled = true/; }' "$BEP_CFG" || true
  echo "✓ BepInEx console enabled"
fi

cat <<EOF

────────────────────────────────────────
Last manual step (Steam can't be edited from a script):

  1. Open Steam on this Mac.
  2. Right-click Cuphead → Properties → General → Launch Options.
  3. Paste, including the quotes:

     "$CUPHEAD_DIR/run_bepinex.sh" %command%

  4. Close the dialog and launch Cuphead from Steam normally.

In-game:
  • Slot Select screen will show a multiplayer lobby UI injected by CupHeads.
  • Use it to host or join your friend's Steam lobby.
  • F11 opens the Local Dev Lab (same-PC simulator) for solo testing.
  • Steam friends list must include the host (cross-platform Steam P2P works
    fine between Windows host and Mac client).
────────────────────────────────────────

If macOS blocks run_bepinex.sh on first launch (Gatekeeper), open Terminal and run:
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR"

Plugin log lives at:
  $CUPHEAD_DIR/BepInEx/LogOutput.log
EOF
