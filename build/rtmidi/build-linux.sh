#!/usr/bin/env bash
set -euo pipefail

# Run on Ubuntu 22.04 with g++ 11.4.0, git, binutils and libasound2-dev installed.
source_checkout="$(realpath "${1:?Usage: build-linux.sh SOURCE_CHECKOUT [OUTPUT_DIRECTORY]}")"
script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output_directory="${2:-$script_directory/../../bin/rtmidi-build/linux}"
pinned_commit=1e5b49925aa60065db52de44c366d446a902547b

if [[ "$(git -C "$source_checkout" rev-parse HEAD)" != "$pinned_commit" ]]; then
    echo "Source checkout must be RtMidi at $pinned_commit." >&2
    exit 1
fi
git -C "$source_checkout" diff --exit-code HEAD --
mkdir -p -- "$output_directory"
output_directory="$(realpath "$output_directory")"
g++ --version
g++ -O2 -fPIC -shared -std=c++11 -D__LINUX_ALSA__ -DRTMIDI_EXPORT \
    "$source_checkout/RtMidi.cpp" "$source_checkout/rtmidi_c.cpp" \
    -lasound -pthread -Wl,-soname,librtmidi.so -o "$output_directory/librtmidi.so"
readelf -d "$output_directory/librtmidi.so"
readelf --version-info "$output_directory/librtmidi.so"
nm -D --defined-only "$output_directory/librtmidi.so"
sha256sum "$output_directory/librtmidi.so"
