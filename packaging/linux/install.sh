#!/bin/sh
# 把下载器装到当前用户目录并加入应用菜单，适用于各 Linux 发行版，不需要 root：
#   程序   ~/.local/bin/CAN/downloader/Downloader
#   命令   ~/.local/bin/can-downloader
#   菜单   ~/.local/share/applications/can-downloader.desktop
# 卸载：./uninstall.sh（保留数据和已安装的软件），兼容 ./install.sh --uninstall
set -eu

here=$(cd "$(dirname "$0")" && pwd)
share=${XDG_DATA_HOME:-$HOME/.local/share}
dir="$HOME/.local/bin/CAN/downloader"
command="$HOME/.local/bin/can-downloader"
desktop="$share/applications/can-downloader.desktop"
icon="$share/icons/hicolor/64x64/apps/can-downloader.png"

if [ "${1:-}" = "--uninstall" ]; then
  shift
  exec sh "$here/uninstall.sh" "$@"
fi

mkdir -p "$dir" "$(dirname "$command")" "$(dirname "$desktop")" "$(dirname "$icon")"
cp "$here/app-logo.png" "$icon"
cp "$here/uninstall.sh" "$dir/uninstall.sh"
chmod 755 "$dir/uninstall.sh"
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
