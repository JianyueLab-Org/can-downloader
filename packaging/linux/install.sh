#!/bin/sh
# 把下载器装到当前用户目录并加入应用菜单，适用于各 Linux 发行版，不需要 root：
#   程序   ~/.local/share/can-downloader/Downloader（目录可写，下载器能自动更新；数据、日志、默认安装目录也在这里）
#   命令   ~/.local/bin/can-downloader
#   菜单   ~/.local/share/applications/can-downloader.desktop
# 卸载：./install.sh --uninstall（保留 ~/.local/share/can-downloader 里的数据和已安装的软件）
set -eu

here=$(cd "$(dirname "$0")" && pwd)
share=${XDG_DATA_HOME:-$HOME/.local/share}
dir="$share/can-downloader"
command="$HOME/.local/bin/can-downloader"
desktop="$share/applications/can-downloader.desktop"

if [ "${1:-}" = "--uninstall" ]; then
  rm -f "$dir/Downloader" "$command" "$desktop"
  echo "已卸载。数据和已安装的软件仍在 $dir，不需要时手动删除。"
  exit 0
fi

mkdir -p "$dir" "$(dirname "$command")" "$(dirname "$desktop")"
# 先复制到临时文件再改名：下载器正在运行时也能覆盖
cp "$here/Downloader" "$dir/Downloader.tmp"
chmod 755 "$dir/Downloader.tmp"
mv -f "$dir/Downloader.tmp" "$dir/Downloader"

# 用包装脚本而不是软链：下载器按程序文件所在目录找数据
printf '#!/bin/sh\nexec "%s" "$@"\n' "$dir/Downloader" > "$command"
chmod 755 "$command"

sed "s|^Exec=.*|Exec=\"$dir/Downloader\"|" "$here/can-downloader.desktop" > "$desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$(dirname "$desktop")" >/dev/null 2>&1 || true

echo "已安装到 $dir"
echo "从应用菜单打开“CAN 下载器”，或在终端运行 can-downloader（~/.local/bin 需要在 PATH 里）。"
