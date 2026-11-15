#!/bin/sh
# 仅卸载 downloader 子目录，不删除 CAN 下其他程序。
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ "$#" -gt 0 ]; then
  applications=$1
elif [ "$(basename "$here")" = Resources ] && [ "$(basename "$(dirname "$here")")" = Contents ]; then
  applications=$(dirname "$(dirname "$(dirname "$here")")")
elif [ -d "$here/CAN Downloader.app" ] && [ "$(basename "$here")" = downloader ]; then
  applications=$here
else
  applications=/Applications/CAN/downloader
fi
target="$applications/CAN Downloader.app"
if [ ! -d "$target" ]; then
  echo "未找到 $target，无需卸载。"
  exit 0
fi
if [ ! -f "$target/Contents/Info.plist" ] ||
   [ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$target/Contents/Info.plist" 2>/dev/null)" != com.jianyuelab.can-downloader ]; then
  echo "目标不是 CAN 下载器，未删除：$target" >&2
  exit 1
fi
if /usr/sbin/lsof -t "$target/Contents/MacOS/Downloader" >/dev/null 2>&1; then
  echo "请先退出 CAN 下载器，再运行卸载脚本。" >&2
  exit 1
fi
rm -rf "$target"
rm -f "$applications/uninstall.sh"
rmdir "$applications" 2>/dev/null || true
rmdir "$(dirname "$applications")" 2>/dev/null || true
echo "已卸载 CAN 下载器。用户数据及已安装的客户端保留在 ~/Library/Application Support/CAN Downloader。"
