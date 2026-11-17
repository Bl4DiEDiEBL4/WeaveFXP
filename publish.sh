#!/usr/bin/env bash
# Build every requested runtime in staging before replacing release artifacts.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
OUT="$ROOT/Release"
PROJ="$ROOT/WeaveFxp.Web/WeaveFxp.Web.csproj"
RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then RIDS=(linux-x64 linux-arm64 win-x64); fi

for RID in "${RIDS[@]}"; do
    case "$RID" in win-x64|linux-x64|linux-arm64) ;; *) echo "Unsupported runtime: $RID" >&2; exit 1 ;; esac
    if [ -L "$OUT" ] || [ -L "$OUT/$RID" ] || [ -L "$OUT/zips" ]; then
        echo "Refusing a linked release path" >&2; exit 1
    fi
done
command -v dotnet >/dev/null || { echo "dotnet SDK is required" >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is required for versioning and release packages" >&2; exit 1; }
VERSION="$(python3 - "$PROJ" <<'PY'
import sys, xml.etree.ElementTree as ET
version = ET.parse(sys.argv[1]).getroot().findtext("./PropertyGroup/Version")
if not version or any(c not in "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-+" for c in version):
    raise SystemExit("Invalid project version")
print(version)
PY
)"
TEMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
WORK="$(mktemp -d "$TEMP_ROOT/weavefxp-publish.XXXXXX")"
cleanup() {
    case "$WORK" in "$TEMP_ROOT"/weavefxp-publish.*) rm -rf -- "$WORK" ;; *) echo "Unsafe staging path" >&2; return 1 ;; esac
}
trap cleanup EXIT

dotnet restore "$PROJ" --disable-parallel
for RID in "${RIDS[@]}"; do
    STAGE="$WORK/$RID"
    NAME=WeaveFXP
    [ "$RID" != win-x64 ] || NAME=WeaveFXP.exe
    dotnet publish "$PROJ" -c Release -r "$RID" --no-restore --self-contained true -m:1 -nr:false \
        -p:BaseOutputPath="$WORK/bin/$RID/" -p:PublishSingleFile=true \
        -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true \
        -p:PublishTrimmed=false -p:DebugType=none -o "$STAGE"
    test -f "$STAGE/$NAME"
    [ "$RID" = win-x64 ] || chmod +x "$STAGE/$NAME"
    python3 - "$STAGE" "$NAME" "$WORK/WeaveFXP-v$VERSION-$RID.zip" <<'PY'
import pathlib, sys, zipfile
root, binary, output = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    zf.write(root / binary, binary)
PY
done

# Runtime data is never moved into temporary staging, even on a failed build.
for RID in "${RIDS[@]}"; do
    DEST="$OUT/$RID"
    NAME=WeaveFXP
    [ "$RID" != win-x64 ] || NAME=WeaveFXP.exe
    if [ -L "$OUT" ] || [ -L "$DEST" ] || [ -L "$OUT/zips" ]; then
        echo "Refusing a linked release path" >&2; exit 1
    fi
    mkdir -p "$DEST" "$OUT/zips"
    for NAME_TO_COPY in "$NAME"; do
        cp "$WORK/$RID/$NAME_TO_COPY" "$DEST/.$NAME_TO_COPY.incoming-$$"
        mv -f -- "$DEST/.$NAME_TO_COPY.incoming-$$" "$DEST/$NAME_TO_COPY"
    done
    for GENERATED in "$DEST"/*.pdb "$DEST"/appsettings*.json "$DEST"/*.staticwebassets*.json "$DEST/web.config" "$DEST/wwwroot" "$DEST/bin" "$DEST/THIRD-PARTY-NOTICES.txt"; do
        case "$GENERATED" in "$OUT/$RID/"*) ;; *) echo "Unsafe generated path" >&2; exit 1 ;; esac
        [ ! -L "$GENERATED" ] || { echo "Refusing linked generated files" >&2; exit 1; }
        rm -rf -- "$GENERATED"
    done
    if [ ! -e "$DEST/data" ] && [ -d "$OUT/data" ]; then cp -a "$OUT/data" "$DEST/data"; fi
    ZIP="WeaveFXP-v$VERSION-$RID.zip"
    cp "$WORK/$ZIP" "$OUT/zips/.$ZIP.incoming-$$"
    mv -f -- "$OUT/zips/.$ZIP.incoming-$$" "$OUT/zips/$ZIP"
    echo "Ready: $DEST/$NAME"
done
echo "Release packages: $OUT/zips"
