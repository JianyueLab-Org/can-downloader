#!/bin/sh
# 只卸载 downloader 子目录中的程序，不影响其他 CAN 客户端。
set -eu
share=${XDG_DATA_HOME:-$HOME/.local/share}
dir="$HOME/.local/bin/CAN/downloader"
case "${1:-}" in
  --keep-data) remove_data=no ;;
  --remove-data) remove_data=yes ;;
  "")
    printf '是否同时删除下载器的设置、日志和缓存？[y/N] '
    answer=
    read -r answer || true
    case "$answer" in y|Y|yes|YES) remove_data=yes ;; *) remove_data=no ;; esac
    ;;
  *) echo "用法：sh uninstall.sh [--keep-data|--remove-data]" >&2; exit 2 ;;
esac
rm -f "$dir/Downloader" "$dir/Downloader.tmp" "$dir/app-logo.png" "$dir/uninstall.sh" \
  "$HOME/.local/bin/can-downloader" "$share/applications/can-downloader.desktop" \
  "$share/icons/hicolor/64x64/apps/can-downloader.png"
if [ "$remove_data" = yes ]; then
  rm -rf "$dir/data" "$dir/logs" "$dir/downloads"
fi
rmdir "$dir" 2>/dev/null || true
rmdir "$HOME/.local/bin/CAN" 2>/dev/null || true
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$share/applications" >/dev/null 2>&1 || true
echo "已卸载下载器。其他 CAN 客户端及已安装的软件保持不变。"
