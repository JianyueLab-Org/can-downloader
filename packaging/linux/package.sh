#!/usr/bin/env bash
# 把 Linux 的单文件发布打成 tar.gz：解压出来是 can-downloader/ 目录——
#   Downloader              程序（自包含，任何 glibc 发行版都能直接运行）
#   install.sh              装到当前用户目录并加入应用菜单（不需要 root）
#   can-downloader.desktop  应用菜单项
#   packaging/linux/package.sh <发布目录> <输出的 .tar.gz>
set -euo pipefail

publish=$1
output=$2
here=$(cd "$(dirname "$0")" && pwd)

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT
mkdir "${staging}/can-downloader"
install -m 755 "${publish}/Downloader" "${staging}/can-downloader/Downloader"
install -m 755 "${here}/install.sh" "${staging}/can-downloader/install.sh"
install -m 644 "${here}/can-downloader.desktop" "${staging}/can-downloader/can-downloader.desktop"

mkdir -p "$(dirname "$output")"
tar -czf "$output" -C "$staging" --owner=0 --group=0 can-downloader
echo "$output"
