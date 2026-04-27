#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NO_INSTALL=0
NO_LOG_CLEANUP=0

for arg in "$@"; do
    case "${arg,,}" in
        -noinstall)    NO_INSTALL=1 ;;
        -nologcleanup) NO_LOG_CLEANUP=1 ;;
        -nodeploy)     NO_INSTALL=1; NO_LOG_CLEANUP=1 ;;
        *) ;;
    esac
done

is_vs_root() {
    [[ -n "${1:-}" && -f "$1/VintagestoryAPI.dll" ]]
}

first_existing_dir() {
    for candidate in "$@"; do
        if [[ -n "$candidate" && -d "$candidate" ]]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    return 1
}

resolve_vintage_story() {
    local candidates=(
        "${VINTAGE_STORY:-}"
        "${VINTAGE_STORY_HOME:-}"
        "${VINTAGE_STORY_PATH:-}"
        "$HOME/Games/vintagestory"
        "$HOME/Games/vintagestory"
        "/mnt/c/Games/VintageStory"
        "/mnt/c/Program Files/Vintage Story"
    )
    for candidate in "${candidates[@]}"; do
        if is_vs_root "$candidate"; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    return 1
}

resolve_vintagestory_data_root() {
    local candidates=(
        "${VINTAGE_STORY_DATA:-}"
        "${VINTAGESTORY_DATA:-}"
        "${XDG_CONFIG_HOME:-$HOME/.config}/VintagestoryData"
        "$HOME/.config/VintagestoryData"
    )
    first_existing_dir "${candidates[@]}"
}

VINTAGE_STORY_DIR="$(resolve_vintage_story || true)"
if [[ -z "$VINTAGE_STORY_DIR" ]]; then
    echo "Error: Vintage Story installation not found (need VintagestoryAPI.dll)"
    exit 1
fi

for cmd in dotnet zip; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "Error: $cmd not found"
        exit 1
    fi
done

export VINTAGE_STORY="$VINTAGE_STORY_DIR"

echo "Building OptiTime..."

cd "$SCRIPT_DIR"
rm -rf bin

dotnet build OptiTime.csproj -c Release -v quiet /p:RestoreSources= /p:RestoreIgnoreFailedSources=true 2>&1 \
    | grep -iE 'warning|error' || true

MOD_VERSION="$(grep -oP '"version"\s*:\s*"\K[^"]+' modinfo.json)"
TFM="$(grep -oP '<TargetFramework>\K[^<]+' OptiTime.csproj)"
RELEASE_DIR="$SCRIPT_DIR/bin/Release/${TFM}"
ZIP_PATH="$SCRIPT_DIR/bin/OptiTime-${MOD_VERSION}.zip"

for file in "$RELEASE_DIR/OptiTime.dll" modinfo.json modicon.png; do
    if [[ ! -f "$file" ]]; then
        echo "Error: Required file missing: $file"
        exit 1
    fi
done

echo "Creating mod package..."
rm -f "$ZIP_PATH"

(
    cd "$SCRIPT_DIR"
    zip -q -9 -j "$ZIP_PATH" \
        "$RELEASE_DIR/OptiTime.dll" \
        modinfo.json \
        modicon.png

    if [[ -d assets ]]; then
        zip -q -9 -r "$ZIP_PATH" assets
    fi
)

if [[ ! -f "$ZIP_PATH" ]]; then
    echo "Error: Package not created"
    exit 1
fi

echo "Build complete: bin/OptiTime-${MOD_VERSION}.zip"

VINTAGE_STORY_DATA_ROOT="$(resolve_vintagestory_data_root || true)"

if [[ "$NO_LOG_CLEANUP" == "0" ]]; then
    CLIENT_LOGS="${VINTAGE_STORY_LOGS:-}"
    if [[ -z "$CLIENT_LOGS" && -n "$VINTAGE_STORY_DATA_ROOT" ]]; then
        CLIENT_LOGS="$VINTAGE_STORY_DATA_ROOT/Logs"
    fi
    if [[ -d "$CLIENT_LOGS" ]]; then
        echo "Cleaning old logs..."
        find "$CLIENT_LOGS" -maxdepth 1 -type f -name '*.log' -delete
    fi
fi

if [[ "$NO_INSTALL" == "0" ]]; then
    CLIENT_MODS="${VINTAGE_STORY_MODS:-}"
    if [[ -z "$CLIENT_MODS" && -n "$VINTAGE_STORY_DATA_ROOT" ]]; then
        CLIENT_MODS="$VINTAGE_STORY_DATA_ROOT/Mods"
    fi
    if [[ -d "$CLIENT_MODS" ]]; then
        rm -f "$CLIENT_MODS"/OptiTime*.zip
        echo "Installing to $CLIENT_MODS..."
        cp -f "$ZIP_PATH" "$CLIENT_MODS/"
        echo "Installed successfully!"
    else
        echo "Warning: Mods folder not found, skipping installation"
    fi
fi
