#!/usr/bin/env bash
# ============================================================================
#  WeaveFXP - produce shippable single-exe builds and release zips.
#
#  Result:  Release/linux-x64/WeaveFXP
#           Release/linux-arm64/WeaveFXP
#           Release/win-x64/WeaveFXP.exe
#           Release/zips/WeaveFXP-v<version>-<runtime>.zip
# ============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/Release"
ZIPOUT="$OUT/zips"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/weavefxp-publish.XXXXXX")"
PROJ="$ROOT/WeaveFxp.Web/WeaveFxp.Web.csproj"
VERSION="1.0.0"

cleanup() {
    rm -rf "$WORK"
}
trap cleanup EXIT

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet SDK not found. Install the .NET 8 SDK:" >&2
    echo "  https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
fi

if command -v python3 >/dev/null 2>&1; then
    VERSION="$(python3 - "$PROJ" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print(root.findtext("./PropertyGroup/Version") or "1.0.0")
PY
)"
fi

mkdir -p "$OUT" "$ZIPOUT"
find "$OUT" -maxdepth 1 -type f -delete

RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then
    RIDS=(linux-x64 linux-arm64 win-x64)
fi

echo "=== Restoring ==="
dotnet restore "$PROJ"

for RID in "${RIDS[@]}"; do
    echo
    echo "=== Publishing $RID ==="

    PRESERVE_DATA="$WORK/data-$RID"
    rm -rf "$PRESERVE_DATA"
    if [ -d "$OUT/$RID/data" ]; then
        mv "$OUT/$RID/data" "$PRESERVE_DATA"
    fi

    rm -rf "$OUT/$RID"
    rm -rf "$WORK/bin/$RID"

    dotnet publish "$PROJ" \
        -c Release \
        -r "$RID" \
        --self-contained true \
        -p:BaseOutputPath="$WORK/bin/$RID/" \
        -p:PublishSingleFile=true \
        -p:IncludeAllContentForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:PublishTrimmed=false \
        -p:DebugType=none \
        -o "$OUT/$RID"

    rm -f "$OUT/$RID"/*.pdb
    rm -f "$OUT/$RID"/appsettings*.json
    rm -f "$OUT/$RID"/*.staticwebassets*.json
    rm -f "$OUT/$RID"/web.config
    rm -rf "$OUT/$RID/wwwroot"
    rm -rf "$OUT/$RID/bin"
    [ -f "$OUT/$RID/WeaveFXP" ] && chmod +x "$OUT/$RID/WeaveFXP"
    [ -d "$PRESERVE_DATA" ] && mv "$PRESERVE_DATA" "$OUT/$RID/data"

    ZIP="$ZIPOUT/WeaveFXP-v$VERSION-$RID.zip"
    rm -f "$ZIP"
    if command -v zip >/dev/null 2>&1; then
        (cd "$OUT/$RID" && zip -q -9 "$ZIP" WeaveFXP*)
    elif command -v python3 >/dev/null 2>&1; then
        python3 - "$OUT/$RID" "$ZIP" <<'PY'
import pathlib
import sys
import zipfile
root = pathlib.Path(sys.argv[1])
zip_path = pathlib.Path(sys.argv[2])
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
    for path in root.glob("WeaveFXP*"):
        if path.is_file():
            zf.write(path, path.name)
PY
    else
        echo "zip or python3 is required to package $RID" >&2
        exit 1
    fi
done

echo
echo "=== Done ==="
ls -lh "$OUT"/*/WeaveFXP* "$ZIPOUT"/WeaveFXP-v"$VERSION"-*.zip 2>/dev/null || true
echo
echo "  Ship only the per-platform zip."
echo "  data/ is created on first run next to the executable."
