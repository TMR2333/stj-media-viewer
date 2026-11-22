#!/usr/bin/env bash
# 一键验证：构建（debug）+ 单元测试 + stjcheck 自检 + 可选打包 dmg。
# 用法：bash scripts/verify.sh [--dmg]
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== 1/4 SwiftPM 清单解析"
swift package dump-package > /dev/null

echo "== 2/4 构建（debug）"
swift build --product STJMediaViewer
swift build --product stjcheck

echo "== 3/4 单元测试"
swift test

echo "== 4/4 stjcheck 数据层自检"
if [[ -d .testdata ]]; then
  swift run -q stjcheck --data-dir .testdata
else
  echo "（跳过：没有 .testdata 样例库，可用 python3 scripts/make-sample-library.py --data-dir .testdata 生成）"
fi

if [[ "${1:-}" == "--dmg" ]]; then
  echo "== 打包 universal dmg"
  bash scripts/build-dmg.sh
fi

echo "全部通过 ✅"
