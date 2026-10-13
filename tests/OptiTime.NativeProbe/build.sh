#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/OptiTime.NativeProbe.csproj"

if [[ -z "${VINTAGE_STORY:-}" || ! -f "$VINTAGE_STORY/VintagestoryAPI.dll" ]]; then
    echo "Set VINTAGE_STORY to an installed Vintage Story directory containing VintagestoryAPI.dll." >&2
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Required command not found: dotnet" >&2
    exit 1
fi

cd "$SCRIPT_DIR"
dotnet build "$PROJECT" -c Release --nologo
