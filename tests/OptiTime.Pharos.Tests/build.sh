#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/OptiTime.Pharos.Tests.csproj"
OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net10.0"

if [[ -z "${VINTAGE_STORY:-}" || ! -f "$VINTAGE_STORY/VintagestoryAPI.dll" ]]; then
    echo "Set VINTAGE_STORY to an installed Vintage Story directory containing VintagestoryAPI.dll." >&2
    exit 1
fi

# Live scenarios render through Mesa llvmpipe by default, which is what CI has.
# Set PHAROS_VGL_DEVICE (for example egl0) to render on a real GPU through
# VirtualGL instead; scenarios that step thousands of frames only finish there.
VGL_DEVICE="${PHAROS_VGL_DEVICE:-}"
required=(dotnet xvfb-run)
if [[ -n "$VGL_DEVICE" ]]; then
    required+=(vglrun)
fi

for cmd in "${required[@]}"; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "Required command not found: $cmd" >&2
        exit 1
    fi
done

export LD_LIBRARY_PATH="$VINTAGE_STORY/Lib:$VINTAGE_STORY:/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export ALSOFT_DRIVERS=null
renderer=()
if [[ -n "$VGL_DEVICE" ]]; then
    renderer=(vglrun -d "$VGL_DEVICE")
else
    export LIBGL_ALWAYS_SOFTWARE=1
    export GALLIUM_DRIVER=llvmpipe
    export MESA_LOADER_DRIVER_OVERRIDE=llvmpipe
    export LIBGL_ALWAYS_INDIRECT=0
    export MESA_GL_VERSION_OVERRIDE="${MESA_GL_VERSION_OVERRIDE:-4.5}"
    export MESA_GLSL_VERSION_OVERRIDE="${MESA_GLSL_VERSION_OVERRIDE:-450}"
fi

dotnet build "$PROJECT" -c Release --nologo

for name in Lib Mods assets; do
    source="$VINTAGE_STORY/$name"
    target="$OUTPUT_DIR/$name"
    if [[ ! -d "$source" ]]; then
        echo "Required Vintage Story directory not found: $source" >&2
        exit 1
    fi

    if [[ -L "$target" ]]; then
        ln -sfn "$source" "$target"
    elif [[ -e "$target" ]]; then
        echo "Refusing to use an existing non-symlink dependency path: $target" >&2
        exit 1
    else
        ln -s "$source" "$target"
    fi
done

# Never render on the desktop session's display; Xvfb provides its own.
env -u DISPLAY xvfb-run -a -s "-screen 0 1280x720x24" \
    "${renderer[@]}" dotnet test "$PROJECT" -c Release --no-build "$@"
