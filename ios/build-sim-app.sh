#!/usr/bin/env bash
# 为 iOS 模拟器构建 .app 并可选安装启动（无需 Xcode 工程，直接用 SwiftPM 源码编译）。
#
# 用法：
#   bash ios/build-sim-app.sh                 # 只构建 dist/STJMobile.app
#   bash ios/build-sim-app.sh --install       # 构建 + 安装到已启动的模拟器并拉起
#   bash ios/build-sim-app.sh --install --demo-library /path/to/.testdata
#                                            # 额外把样例库复制进 App 容器（沙箱演示数据）
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"
OUT="$ROOT/dist"
APP="$OUT/STJMobile.app"
SDK="$(xcrun --sdk iphonesimulator --show-sdk-path)"
TARGET="arm64-apple-ios17.0-simulator"
INSTALL=0
DEMO_LIBRARY=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) INSTALL=1; shift ;;
    --demo-library) DEMO_LIBRARY="$2"; shift 2 ;;
    *) echo "未知参数：$1" >&2; exit 1 ;;
  esac
done

echo "▶ 清理输出目录"
rm -rf "$APP"
mkdir -p "$APP"

echo "▶ 编译 STJCore（${TARGET}）"
BUILD="$ROOT/.build/ios-sim"
rm -rf "$BUILD" && mkdir -p "$BUILD"
xcrun swiftc \
  -sdk "$SDK" -target "$TARGET" -parse-as-library -O -wmo \
  -module-name STJCore \
  -emit-module-path "$BUILD/STJCore.swiftmodule" \
  -c -o "$BUILD/STJCore.o" \
  "$ROOT"/Sources/STJCore/*.swift \
  "$ROOT"/Sources/STJCore/Platform/*.swift

echo "▶ 编译 STJMobile + App（${TARGET}）"
xcrun swiftc \
  -sdk "$SDK" -target "$TARGET" -parse-as-library -O -wmo \
  -I "$BUILD" \
  -module-name STJMobile \
  -c -o "$BUILD/STJMobile.o" \
  "$ROOT"/ios/Sources/STJMobile/*.swift \
  "$ROOT"/ios/App/STJMobileApp.swift

echo "▶ 链接"
xcrun swiftc \
  -sdk "$SDK" -target "$TARGET" \
  -o "$APP/STJMobile" \
  "$BUILD/STJCore.o" "$BUILD/STJMobile.o" \
  -lsqlite3

echo "▶ 组装 .app"
cp "$ROOT/ios/App/Info.plist" "$APP/Info.plist"
cp "$ROOT/Resources/AppIcon.png" "$APP/AppIcon.png" 2>/dev/null || true
# 生成图标集（iOS 需要 Assets.car 才能显示图标，这里退化为 Info.plist 引用原始 png 也能跑）
/usr/libexec/PlistBuddy -c "Delete :CFBundleIconFile" "$APP/Info.plist" 2>/dev/null || true
/usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon.png" "$APP/Info.plist" 2>/dev/null || true
codesign --force --sign - --timestamp=none "$APP" >/dev/null 2>&1 || true

if [[ -n "$DEMO_LIBRARY" ]]; then
  echo "▶ 打包演示素材库（$(basename "${DEMO_LIBRARY}")）"
  mkdir -p "$APP/DemoLibrary"
  cp -R "$DEMO_LIBRARY"/. "$APP/DemoLibrary/"
  # 演示库内的绝对路径按容器路径重写，避免指向开发机 /private/tmp
  sqlite3 "$APP/DemoLibrary/catalog.db" <<'SQL' 2>/dev/null || true
UPDATE assets SET full_path = replace(full_path, '/private/tmp/', './') WHERE full_path LIKE '/private/tmp/%';
SQL
fi

echo "▶ 完成：${APP}"
if [[ $INSTALL -eq 1 ]]; then
  BOOTED="$(xcrun simctl list devices booted | grep -E '\(Booted\)' | head -1 | sed -E 's/.*\(([0-9A-F-]+)\).*/\1/')"
  if [[ -z "$BOOTED" ]]; then
    echo "✗ 没有已启动的模拟器，先执行：xcrun simctl boot <设备名>" >&2
    exit 1
  fi
  echo "▶ 安装到模拟器 ${BOOTED}"
  xcrun simctl install "$BOOTED" "$APP"
  xcrun simctl launch "$BOOTED" com.stjarnaperspective.stjmediaviewer.mobile || true
fi
