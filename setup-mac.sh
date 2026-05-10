#!/usr/bin/env bash
# Mac installer for the CupHeads BepInEx plugin. The upstream Electron installer
# is Windows-only; this is the equivalent for native macOS Cuphead.
#
# Usage: ./setup-mac.sh [path-to-Cuphead-folder]

set -euo pipefail

CUPHEAD_DIR="${1:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# 5.4.23.5 unix preloader crashes with DllNotFoundException: libc.so.6 on macOS
# (its Linux uname() probe trips before the OSX fallback). 5.4.22 catches and
# falls through cleanly.
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_unix_5.4.22.0.zip"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Find the DLL (next to the script, in a CupheadOnline/ folder, or in a tarball).
PLUGIN_DLL=""
if [[ -f "$SCRIPT_DIR/CupheadOnline.dll" ]]; then
  PLUGIN_DLL="$SCRIPT_DIR/CupheadOnline.dll"
elif [[ -f "$SCRIPT_DIR/CupheadOnline/CupheadOnline.dll" ]]; then
  PLUGIN_DLL="$SCRIPT_DIR/CupheadOnline/CupheadOnline.dll"
else
  for tarball in "$SCRIPT_DIR"/CupHeads-mac*.tar.gz; do
    [[ -f "$tarball" ]] || continue
    tar -xzf "$tarball" -C "$TMP/"
    for try in "$TMP"/CupHeads-mac/CupheadOnline.dll \
               "$TMP"/CupHeads-mac/CupheadOnline/CupheadOnline.dll; do
      [[ -f "$try" ]] && PLUGIN_DLL="$try" && break
    done
    [[ -n "$PLUGIN_DLL" ]] && break
  done
fi
if [[ -z "$PLUGIN_DLL" ]]; then
  echo "error: drop CupheadOnline.dll (or CupHeads-mac.tar.gz) next to this script" >&2
  exit 1
fi
echo "✓ DLL: $PLUGIN_DLL"

# Find Cuphead.
if [[ -z "$CUPHEAD_DIR" ]]; then
  for c in \
    "$HOME/Library/Application Support/Steam/steamapps/common/Cuphead" \
    "/Applications/Steam/steamapps/common/Cuphead"; do
    [[ -d "$c" ]] && CUPHEAD_DIR="$c" && break
  done
  if [[ -z "$CUPHEAD_DIR" ]]; then
    LIB="$HOME/Library/Application Support/Steam/steamapps/libraryfolders.vdf"
    if [[ -f "$LIB" ]]; then
      while IFS= read -r path; do
        local_path="${path}/steamapps/common/Cuphead"
        [[ -d "$local_path" ]] && CUPHEAD_DIR="$local_path" && break
      done < <(grep -oE '"path"[[:space:]]*"[^"]*"' "$LIB" | sed -E 's/.*"path"[[:space:]]*"([^"]*)".*/\1/')
    fi
  fi
fi
if [[ -z "$CUPHEAD_DIR" || ! -d "$CUPHEAD_DIR" ]]; then
  echo "error: pass Cuphead folder as arg, e.g. \"\$HOME/Library/Application Support/Steam/steamapps/common/Cuphead\"" >&2
  exit 2
fi
[[ -d "$CUPHEAD_DIR/Cuphead.app" ]] || { echo "error: Cuphead.app not in $CUPHEAD_DIR (this script is native-Mac only, not Wine)" >&2; exit 3; }
echo "✓ Cuphead: $CUPHEAD_DIR"

# BepInEx if missing.
if [[ ! -f "$CUPHEAD_DIR/run_bepinex.sh" || ! -d "$CUPHEAD_DIR/BepInEx/core" ]]; then
  echo "→ installing BepInEx 5.4.22"
  curl -L --fail --progress-bar -o "$TMP/bep.zip" "$BEPINEX_URL"
  ditto -x -k "$TMP/bep.zip" "$TMP/bep"
  /bin/cp -R "$TMP/bep/" "$CUPHEAD_DIR/"
  chmod +x "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  /usr/bin/sed -i '' 's/^executable_name=""$/executable_name="Cuphead.app"/' "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR/run_bepinex.sh" "$CUPHEAD_DIR/BepInEx" 2>/dev/null || true
fi

# Mono dllmap fix: Cuphead's stock config remaps "libc" but BepInEx specifically
# DllImports "libc.so.6" so we need that exact name remapped to libSystem.
for cfg in \
  "$CUPHEAD_DIR/Cuphead.app/Contents/Mono/etc/mono/config" \
  "$CUPHEAD_DIR/Cuphead.app/Contents/MonoBleedingEdge/etc/mono/config"; do
  if [[ -f "$cfg" ]] && ! grep -q 'libc.so.6' "$cfg"; then
    /usr/bin/sed -i '' \
      -e '/<dllmap dll="libc" target="libc.dylib"/a\
\	<dllmap dll="libc.so.6" target="/usr/lib/libSystem.dylib" os="osx"/>' "$cfg"
    echo "✓ patched $cfg"
  fi
done

# Drop the DLL.
PLUGIN_DEST="$CUPHEAD_DIR/BepInEx/plugins/CupheadOnline"
mkdir -p "$PLUGIN_DEST"
/bin/cp -f "$PLUGIN_DLL" "$PLUGIN_DEST/CupheadOnline.dll"
xattr -dr com.apple.quarantine "$PLUGIN_DEST" 2>/dev/null || true
echo "✓ $PLUGIN_DEST/CupheadOnline.dll"

# Pre-seed config: kill the startup splash video, leave F11 dev lab on.
mkdir -p "$CUPHEAD_DIR/BepInEx/config"
cat > "$CUPHEAD_DIR/BepInEx/config/com.cupheadonline.mod.cfg" <<'EOF'
[Debug]
EnableLocalDevSessionHotkey = true

[StartupSplash]
EnableStartupSplash = false

[Networking]
LatencyFriendlyDamage = true
EOF

# Console output to launching Terminal.
BEP_CFG="$CUPHEAD_DIR/BepInEx/config/BepInEx.cfg"
[[ -f "$BEP_CFG" ]] && /usr/bin/sed -i '' '/^\[Logging\.Console\]/,/^\[/ { s/^Enabled = false$/Enabled = true/; }' "$BEP_CFG" || true

cat <<EOF

────────────────────────────────────────
Last step (Steam can't be edited from a script):

  Steam → Cuphead → Properties → Launch Options → paste:

    "$CUPHEAD_DIR/run_bepinex.sh" %command%

Then launch Cuphead from Steam. F11 in Slot Select opens the dev lab.

If macOS blocks run_bepinex.sh on first launch:
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR"

Log: $CUPHEAD_DIR/BepInEx/LogOutput.log
EOF
