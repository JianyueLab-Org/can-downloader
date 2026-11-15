#!/bin/sh
# 每个程序一个子目录，默认安装到 /Applications/CAN/downloader。
set -eu
here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
applications=${1:-/Applications/CAN/downloader}
source="$here/CAN Downloader.app"
target="$applications/CAN Downloader.app"

if [ ! -f "$source/Contents/MacOS/Downloader" ]; then
  echo "找不到 CAN Downloader.app，请先完整解压安装包。" >&2
  exit 1
fi
mkdir -p "$applications"
if [ ! -w "$applications" ]; then
  echo "没有权限写入 $applications，可运行：sh install.sh \"\$HOME/Applications/CAN/downloader\"" >&2
  exit 1
fi
if [ -e "$target/Contents/MacOS/Downloader" ] &&
   /usr/sbin/lsof -t "$target/Contents/MacOS/Downloader" >/dev/null 2>&1; then
  echo "请先退出 CAN 下载器，再运行安装脚本。" >&2
  exit 1
fi

staging=$(mktemp -d "$applications/.can-downloader-install.XXXXXX")
trap 'rm -rf "$staging"' EXIT HUP INT TERM
ditto "$source" "$staging/CAN Downloader.app"
if [ -e "$target" ]; then
  mv "$target" "$staging/previous.app"
fi
if ! mv "$staging/CAN Downloader.app" "$target"; then
  if [ -e "$staging/previous.app" ]; then mv "$staging/previous.app" "$target"; fi
  exit 1
fi
install -m 755 "$here/uninstall.sh" "$applications/uninstall.sh"
echo "已安装到 $target"
echo "卸载：sh \"$target/Contents/Resources/uninstall.sh\" \"$applications\""
