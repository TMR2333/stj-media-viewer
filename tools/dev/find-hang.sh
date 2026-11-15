#!/usr/bin/env bash
# 找出元数据解析会挂住的样本文件
cd "$(dirname "$0")/../.." || exit 1
EXE="tools/MetaSmoke/bin/Debug/net10.0-windows10.0.19041.0/win-x64/MetaSmoke.exe"
LIB="tools/SampleLibrary/out/library"
HANG=0
while IFS= read -r f; do
  META_DEBUG="$(pwd)/$f" timeout 6 "$EXE" > /tmp/metaprobe.txt 2>&1
  rc=$?
  if [ "$rc" -eq 124 ]; then
    echo "HANG(>6s): $f"
    HANG=$((HANG+1))
  elif [ "$rc" -ne 0 ]; then
    echo "rc=$rc: $f"
  fi
done < <(find "$LIB" -type f \( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.tif' -o -iname '*.tiff' -o -iname '*.arw' -o -iname '*.cr2' -o -iname '*.nef' -o -iname '*.dng' -o -iname '*.raf' -o -iname '*.mp4' -o -iname '*.mov' -o -iname '*.mkv' -o -iname '*.wav' -o -iname '*.mp3' -o -iname '*.flac' -o -iname '*.m4a' \))
echo "hangs=$HANG"