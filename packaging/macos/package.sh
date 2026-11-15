#!/usr/bin/env bash
# 将 .app 和安装 / 卸载脚本放在 zip 根目录，兼容自更新查找 .app。
set -euo pipefail
app=$1
output=$2
here=$(cd "$(dirname "$0")" && pwd)
staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT
ditto "$app" "$staging/CAN Downloader.app"
install -m 755 "$here/install.sh" "$staging/install.sh"
install -m 755 "$here/uninstall.sh" "$staging/uninstall.sh"
mkdir -p "$(dirname "$output")"
rm -f "$output"
ditto -c -k "$staging" "$output"
echo "$output"
