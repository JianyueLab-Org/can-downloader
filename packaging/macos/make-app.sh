#!/usr/bin/env bash
# 把 macOS 的单文件发布做成 CAN Downloader.app（只能在 macOS 上跑：要用 clang、codesign）。
#   packaging/macos/make-app.sh <输出的 .app 路径> <版本号> <arm64 发布目录> [<x64 发布目录>]
#
# 给了两个发布目录就做通用包（Apple Silicon 和 Intel 都能直接运行）：Contents/MacOS 里放
# Downloader-arm64、Downloader-x64 两份单文件程序，外加通用的小入口 Downloader（launcher.c），按本机芯片
# exec 对应的那份。.NET 单文件程序不能用 lipo 合并，所以只能这样。只给一个目录就是单架构包。
#
# 下载器在 .app 里运行时，数据、日志、下载缓存和默认安装目录放 ~/Library/Application Support/CAN Downloader
# （见 AppPaths）；自更新替换整个 .app（见 SelfUpdater）。入口名 Downloader 与 SelfUpdater.BundleExecutable 一致。
# 没有开发者证书，做 ad-hoc 签名：Apple Silicon 要求所有代码至少有 ad-hoc 签名。
set -euo pipefail

app=$1
version=$2
arm64=$3
x64=${4:-}
here=$(cd "$(dirname "$0")" && pwd)

rm -rf "$app"
mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Resources"

if [ -n "$x64" ]; then
  cp "${arm64}/Downloader" "${app}/Contents/MacOS/Downloader-arm64"
  cp "${x64}/Downloader" "${app}/Contents/MacOS/Downloader-x64"
  chmod +x "${app}/Contents/MacOS/Downloader-arm64" "${app}/Contents/MacOS/Downloader-x64"
  clang -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 \
    -o "${app}/Contents/MacOS/Downloader" "${here}/launcher.c"
else
  cp "${arm64}/Downloader" "${app}/Contents/MacOS/Downloader"
fi
chmod +x "${app}/Contents/MacOS/Downloader"

cat > "${app}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>com.jianyuelab.can-downloader</string>
  <key>CFBundleName</key>
  <string>CAN Downloader</string>
  <key>CFBundleDisplayName</key>
  <string>CAN 下载器</string>
  <key>CFBundleExecutable</key>
  <string>Downloader</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${version}</string>
  <key>CFBundleVersion</key>
  <string>${version}</string>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$app"
echo "$app"
