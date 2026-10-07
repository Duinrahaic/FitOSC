#!/usr/bin/env bash
set -euo pipefail

fail() { printf '%s\n' "$*" >&2; exit 1; }
[[ $# == 3 || $# == 5 ]] || fail 'Usage: package.sh PUBLISH_DIRECTORY OUTPUT_DIRECTORY VERSION [APPIMAGETOOL RUNTIME]'
version=$3
[[ $version =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]] || fail 'VERSION must be a version such as 2.1.0 or 2.1.0-beta.1 (without v).'
for command in realpath mktemp cp chmod find od sha256sum desktop-file-validate file; do
    command -v "$command" >/dev/null || fail "Required command missing: $command"
done
publish=$(realpath -e -- "$1")
[[ -d $publish && -f $publish/FitOSC && -f $publish/Assets/icon.png ]] || fail 'Publish directory must contain FitOSC and Assets/icon.png.'
output=$(realpath -m -- "$2")
[[ $publish != / && $output != / ]] || fail 'Publish and output directories must not be filesystem root.'
[[ $output != "$publish" && $output != "$publish/"* && $publish != "$output/"* ]] || fail 'Publish and output directories must not contain one another.'
destination="$output/FitOSC-$version-x86_64.AppImage"
[[ ! -e $destination && ! -L $destination ]] || fail "Output already exists: $destination"
export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-0}
[[ $SOURCE_DATE_EPOCH =~ ^[0-9]+$ ]] || fail 'SOURCE_DATE_EPOCH must be a nonnegative integer.'

# This is the only directory removed by the script, and it is created here.
temporary=$(mktemp -d /tmp/fitosc-appimage.XXXXXXXXXX)
export TMPDIR="$temporary"
cleanup() {
    [[ $temporary == /tmp/fitosc-appimage.* && -d $temporary && ! -L $temporary ]] || return
    rm -rf -- "$temporary"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
tool_sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
mkdir -- "$temporary/tools"
tool="$temporary/tools/appimagetool-x86_64.AppImage"
runtime="$temporary/tools/runtime-x86_64"
if [[ $# == 5 ]]; then
    cp -- "$(realpath -e -- "$4")" "$tool"
    cp -- "$(realpath -e -- "$5")" "$runtime"
else
    command -v curl >/dev/null || fail 'Required command missing: curl'
    curl --fail --location --proto '=https' --proto-redir '=https' --output "$tool" \
        https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
    curl --fail --location --proto '=https' --proto-redir '=https' --output "$runtime" \
        https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
fi
printf '%s  %s\n' "$tool_sha" "$tool" "$runtime_sha" "$runtime" | sha256sum --check --strict
chmod 755 -- "$tool" "$runtime"

appdir="$temporary/AppDir"
mkdir -p -- "$appdir/usr/bin"
cp -a -- "$publish/." "$appdir/usr/bin/"
find "$appdir" -type d -exec chmod 755 -- {} +
# Preserve executable entrypoints; make every ELF native binary executable even
# when the publish directory was copied through a filesystem that lost modes.
while IFS= read -r -d '' file; do
    magic=$(od -An -tx1 -N4 -- "$file")
    if [[ -x $file || $magic == ' 7f 45 4c 46' ]]; then
        chmod 755 -- "$file"
    else
        chmod 644 -- "$file"
    fi
done < <(find "$appdir/usr/bin" -type f -print0)
chmod 755 -- "$appdir/usr/bin/FitOSC"
ln -s usr/bin/FitOSC "$appdir/AppRun"
cp -- "$publish/Assets/icon.png" "$appdir/fitosc.png"
chmod 644 -- "$appdir/fitosc.png"
ln -s fitosc.png "$appdir/.DirIcon"
cat > "$appdir/FitOSC.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=FitOSC
Exec=FitOSC
Icon=fitosc
Terminal=false
Categories=Utility;
StartupWMClass=FitOSC
DESKTOP
desktop-file-validate "$appdir/FitOSC.desktop"
# Keep the tool's extracted files inside the task-owned workspace as well.
cd -- "$temporary"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --runtime-file "$runtime" \
    "$appdir" "$temporary/FitOSC.AppImage"
chmod 755 -- "$temporary/FitOSC.AppImage"
mkdir -p -- "$output"
cp --no-clobber -- "$temporary/FitOSC.AppImage" "$destination"
printf 'AppImage: %s\n' "$destination"
