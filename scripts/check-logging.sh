#!/usr/bin/env sh
set -eu
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../src" && pwd)
violations=$(find "$source_dir" -type f -name '*.cs' ! -path '*/obj/*' ! -path '*/bin/*' ! -name 'Log.cs' ! -name 'Log.*.cs' \
    -exec grep -nE '\.Log(Trace|Debug|Information|Warning|Error|Critical)[[:space:]]*\(|\bLog[A-Za-z0-9_]*[[:space:]]*\([[:space:]]*\$' /dev/null {} + || :)
if [ -n "$violations" ]; then
    printf '%s\n' "$violations"
    exit 1
fi
printf '%s\n' 'Logging guard passed.'
