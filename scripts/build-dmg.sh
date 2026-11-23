#!/usr/bin/env bash
# STJ Media Viewer · macOS 构建脚本
#
# 在 macOS（Xcode Command Line Tools 已装）上执行：
#   bash scripts/build-dmg.sh
#
# 产出：
#   dist/STJ Media Viewer.app
#   dist/STJMediaViewer-<version>-macos-<arch>.dmg
#
# 可选环境变量：
#   VERSION=1.25.0                版本号
#   CODESIGN_IDENTITY="Developer ID Application: ..."   正式签名（不填则 ad-hoc）
#   NOTARY_PROFILE=stj-notary     notarytool 的 keychain profile 名（填写则自动公证 + staple）
#   ARCHS="arm64 x86_64"          构建架构（默认 universal）

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSION="${VERSION:-1.42.1}"
APP_NAME="STJ Media Viewer"
BUNDLE_NAME="STJMediaViewer.app"
DIST="$ROOT/dist"
APP="$DIST/$BUNDLE_NAME"
ARCHS="${ARCHS:-arm64 x86_64}"

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "error: 这个脚本必须在 macOS 上运行（需要 Xcode 工具链）。" >&2
    exit 1
fi

echo "[1/6] swift build ($ARCHS)"
SWIFT_ARCH_FLAGS=()
for arch in $ARCHS; do
    SWIFT_ARCH_FLAGS+=(--arch "$arch")
done
swift build -c release "${SWIFT_ARCH_FLAGS[@]}"
BIN_DIR="$(swift build -c release "${SWIFT_ARCH_FLAGS[@]}" --show-bin-path)"

echo "[2/6] assemble $BUNDLE_NAME"
rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/STJMediaViewer" "$APP/Contents/MacOS/STJMediaViewer"
cp Resources/Info.plist "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/STJMediaViewer"

echo "[3/6] app icon"
ICONSET="$DIST/AppIcon.iconset"
mkdir -p "$ICONSET"
if [ -f Resources/AppIcon.png ]; then
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" Resources/AppIcon.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
        double=$((size * 2))
        sips -z "$double" "$double" Resources/AppIcon.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
    rm -rf "$ICONSET"
else
    echo "  (skip: Resources/AppIcon.png 不存在)"
fi

echo "[4/6] code signing"
if [ -n "${CODESIGN_IDENTITY:-}" ]; then
    codesign --force --deep --options runtime --timestamp \
        --sign "$CODESIGN_IDENTITY" "$APP"
    echo "  signed with: $CODESIGN_IDENTITY"
else
    codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
    echo "  ad-hoc signed（用户首次打开需右键 → 打开）"
fi

echo "[5/6] create dmg"
STAGE="$DIST/dmg-stage"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
ARCH_LABEL="$(echo $ARCHS | tr ' ' '-')"
DMG="$DIST/STJMediaViewer-${VERSION}-macos-${ARCH_LABEL}.dmg"
hdiutil create -volname "$APP_NAME $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

echo "[6/6] notarization"
if [ -n "${NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    echo "  notarized + stapled"
else
    echo "  (skip: 没设置 NOTARY_PROFILE)"
fi

echo ""
echo "done:"
ls -lh "$DIST"/*.dmg | awk '{print "  " $9 " (" $5 ")"}'