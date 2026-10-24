#!/usr/bin/env bash
# 在 macOS / Linux 上发布并打包下载器（Windows 用 dotnet publish src/Downloader -p:PublishProfile=win-x64）。
#   packaging/build.sh               按本机系统：macOS 出 Apple Silicon 和 Intel 通用的 .app，Linux 出 tar.gz
#   packaging/build.sh linux-arm64   指定目标：osx-universal / osx-arm64 / osx-x64 / linux-x64 / linux-arm64
# 输出到 dist/，文件名与 GitHub Release 上的一致：CAN-Downloader-<版本>-<目标>.zip（macOS）/ .tar.gz（Linux）。
# macOS 版只能在 macOS 上打（要用 clang、ditto、codesign）。
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"

target=${1:-}
if [ -z "$target" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-*) target=osx-universal ;;
    Linux-x86_64) target=linux-x64 ;;
    Linux-aarch64) target=linux-arm64 ;;
    *) echo "不支持的系统：$(uname -s) $(uname -m)" >&2; exit 2 ;;
  esac
fi

version=$(dotnet msbuild src/Downloader/Downloader.csproj -getProperty:Version | tr -d '[:space:]')
mkdir -p dist

publish() {
  dotnet publish src/Downloader -p:PublishProfile="$1"
}

case "$target" in
  osx-universal | osx-arm64 | osx-x64)
    if [ "$target" = osx-universal ]; then
      publish osx-arm64
      publish osx-x64
      dirs=(publish/osx-arm64 publish/osx-x64)
    else
      publish "$target"
      dirs=("publish/${target}")
    fi
    bash packaging/macos/make-app.sh "dist/CAN Downloader.app" "$version" "${dirs[@]}"
    out="CAN-Downloader-${version}-${target}.zip"
    rm -f "dist/${out}"
    (cd dist && ditto -c -k --keepParent "CAN Downloader.app" "$out")
    ;;
  linux-x64 | linux-arm64)
    publish "$target"
    out="CAN-Downloader-${version}-${target}.tar.gz"
    bash packaging/linux/package.sh "publish/${target}" "dist/${out}"
    ;;
  *)
    echo "未知目标：${target}" >&2
    exit 2
    ;;
esac

echo "已输出 dist/${out}"
