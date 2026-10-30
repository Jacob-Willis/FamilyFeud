#!/usr/bin/env bash
# Pull, build and run the Mac Catalyst build of Family Feud.
# Usage: ./scripts/mac.sh [run|build|publish|clean] [--no-pull]
set -euo pipefail

TFM="net10.0-maccatalyst"

usage() {
    cat <<USAGE
Usage: $(basename "$0") [command] [--no-pull]

Commands:
  run       Pull, build and launch the app (default)
  build     Pull and build only
  publish   Pull and publish a Release build, then show where it landed
  clean     Delete bin/ and obj/, then run

Options:
  --no-pull   Skip 'git pull --ff-only'
  -h, --help  Show this help
USAGE
}

COMMAND=""
PULL=1

while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        --no-pull)
            PULL=0
            ;;
        run|build|publish|clean)
            if [ -n "$COMMAND" ]; then
                echo "Only one command allowed (got '$COMMAND' and '$1')." >&2
                usage >&2
                exit 1
            fi
            COMMAND="$1"
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
    shift
done

COMMAND="${COMMAND:-run}"

cd "$(dirname "$0")/.."

check_prereqs() {
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "dotnet not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download" >&2
        exit 1
    fi

    if ! dotnet workload list 2>/dev/null | grep -q "maui"; then
        echo "The MAUI workload isn't installed. Install it with:" >&2
        echo "  sudo dotnet workload install maui" >&2
        exit 1
    fi

    if [ ! -f appsettings.json ]; then
        echo "appsettings.json is missing. Copy appsettings.example.json to appsettings.json" >&2
        echo "and fill in the Hasura endpoint and admin secret:" >&2
        echo "  cp appsettings.example.json appsettings.json" >&2
        exit 1
    fi
}

pull() {
    if [ "$PULL" -eq 1 ]; then
        echo "==> git pull --ff-only"
        git pull --ff-only
    else
        echo "==> Skipping git pull"
    fi
}

do_run() {
    pull
    check_prereqs
    echo "==> Building and launching ($TFM)"
    dotnet build -t:Run -f "$TFM"
}

do_build() {
    pull
    check_prereqs
    echo "==> Building ($TFM)"
    dotnet build -f "$TFM"
}

do_publish() {
    pull
    check_prereqs
    echo "==> Publishing Release ($TFM)"
    dotnet publish -f "$TFM" -c Release

    out_dir="bin/Release/$TFM"
    echo "==> Output:"
    found="$(find "$out_dir" \( -name "*.pkg" -o -name "*.app" \) -prune -print 2>/dev/null || true)"
    if [ -n "$found" ]; then
        echo "$found" | while IFS= read -r item; do
            echo "  $(pwd)/$item"
        done
    else
        echo "  No .pkg or .app found under $out_dir"
    fi
}

do_clean() {
    echo "==> Removing bin/ and obj/"
    rm -rf bin obj
    do_run
}

case "$COMMAND" in
    run) do_run ;;
    build) do_build ;;
    publish) do_publish ;;
    clean) do_clean ;;
esac
