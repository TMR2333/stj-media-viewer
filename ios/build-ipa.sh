#!/usr/bin/env bash
# 为 iPhone / iPad 真机构建 .ipa（无需 Xcode 工程，直接用 SwiftPM 源码 + iphoneos SDK 编译）。
#
# 默认产出「未签名」IPA：结构完整、可直接被 Sideloadly / AltStore / Xcode 个人免费账号重签安装。
# 若你本机已有 Apple 开发证书与描述文件，可加 --sign / --profile 产出签名版。
#
# 用法：
#   bash ios/build-ipa.sh
#   bash ios/build-ipa.sh --sign "Apple Development: you@example.com (TEAMID)" \
#                         --profile ~/Downloads/STJMobile.mobileprovision
#   bash ios/build-ipa.sh --output dist/STJMobile-0.1.1.ipa
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
OUT_DIR="$ROOT/dist"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' ios/App/Info.plist)"
BUILD_NUMBER="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' ios/App/Info.plist)"
BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' ios/App/Info.plist)"
IPA="$OUT_DIR/STJMobile-${VERSION}-unsigned.ipa"
SIGN_IDENTITY=""
PROFILE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sign) SIGN_IDENTITY="$2"; shift 2 ;;
    --profile) PROFILE="$2"; shift 2 ;;
    --output) IPA="$2"; shift 2 ;;
    *) echo "未知参数：$1" >&2; exit 1 ;;
  esac
done

SDK="$(xcrun --sdk iphoneos --show-sdk-path)"
TARGET="arm64-apple-ios17.0"
BUILD="$ROOT/.build/ios-device"
APP_DIR="$BUILD/Payload/STJMediaViewer.app"

echo "▶ 目标：${TARGET}（SDK：${SDK}）"
rm -rf "$BUILD" && mkdir -p "$APP_DIR"

echo "▶ 编译 STJCore（设备）"
xcrun swiftc \
  -sdk "$SDK" -target "$TARGET" -parse-as-library -O -wmo \
  -module-name STJCore \
  -emit-module-path "$BUILD/STJCore.swiftmodule" \
  -c -o "$BUILD/STJCore.o" \
  "$ROOT"/Sources/STJCore/*.swift \
  "$ROOT"/Sources/STJCore/Platform/*.swift

echo "▶ 编译 STJMobile + App（设备）"
xcrun swiftc \
  -sdk "$SDK" -target "$TARGET" -parse-as-library -O -wmo \
  -I "$BUILD" \
  -module-name STJMobile \
  -c -o "$BUILD/STJMobile.o" \
  "$ROOT"/ios/Sources/STJMobile/*.swift \
  "$ROOT"/ios/App/STJMobileApp.swift

echo "▶ 链接可执行文件"
xcrun swiftc \
  -sdk "$SDK" -target "$TARGET" \
  -o "$APP_DIR/STJMobile" \
  "$BUILD/STJCore.o" "$BUILD/STJMobile.o" \
  -lsqlite3

echo "▶ 组装 App 包"
cp "$ROOT/ios/App/Info.plist" "$APP_DIR/Info.plist"
/usr/libexec/PlistBuddy -c "Delete :CFBundleIconFile" "$APP_DIR/Info.plist" 2>/dev/null || true
cp "$ROOT/ios/App/AppIcon.png" "$APP_DIR/AppIcon.png" 2>/dev/null || cp "$ROOT/Resources/AppIcon.png" "$APP_DIR/AppIcon.png" 2>/dev/null || true
/usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon.png" "$APP_DIR/Info.plist" 2>/dev/null || true
/usr/libexec/PlistBuddy -c "Add :MinimumOSVersion string 17.0" "$APP_DIR/Info.plist" 2>/dev/null || \
  /usr/libexec/PlistBuddy -c "Set :MinimumOSVersion 17.0" "$APP_DIR/Info.plist"

if [[ -n "$SIGN_IDENTITY" ]]; then
  if [[ -z "$PROFILE" || ! -f "$PROFILE" ]]; then
    echo "✗ 签名需要 --profile 指向有效的 .mobileprovision" >&2
    exit 1
  fi
  echo "▶ 嵌入描述文件并签名（${SIGN_IDENTITY}）"
  cp "$PROFILE" "$APP_DIR/embedded.mobileprovision"
  # 从描述文件里取出 entitlements（application-identifier 等）
  ENTITLEMENTS="$BUILD/entitlements.plist"
  security cms -D -i "$PROFILE" > "$BUILD/profile.plist"
  /usr/libexec/PlistBuddy -x -c 'Print :Entitlements' "$BUILD/profile.plist" > "$ENTITLEMENTS"
  codesign --force --sign "$SIGN_IDENTITY" --entitlements "$ENTITLEMENTS" \
    --timestamp=none "$APP_DIR" >/dev/null
  echo "▶ 签名完成"
else
  echo "▶ 未签名模式：文件保持原样，安装时由 Sideloadly / AltStore / Xcode 重新签名"
fi

echo "▶ 打包 ipa"
mkdir -p "$OUT_DIR"
rm -f "$IPA"
(cd "$BUILD" && zip -qry "$IPA" Payload)

echo "▶ 完成：${IPA}（$(du -h "$IPA" | cut -f1)）"
echo "  Bundle ID：$BUNDLE_ID  版本：$VERSION ($BUILD_NUMBER)"
echo "  安装：Sideloadly/AltStore 用你自己的 Apple ID 重签后装到设备"