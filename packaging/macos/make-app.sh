#!/usr/bin/env bash
# 把 macOS ARM64 的单文件发布做成 CAN Downloader.app（只能在 macOS 上跑：要用 codesign）。
#   packaging/macos/make-app.sh <输出的 .app 路径> <版本号> <arm64 发布目录>
#
# 下载器在 .app 里运行时，数据、日志、下载缓存和默认安装目录放 ~/Library/Application Support/CAN Downloader
# （见 AppPaths）；自更新替换整个 .app（见 SelfUpdater）。入口名 Downloader 与 SelfUpdater.BundleExecutable 一致。
# 没有开发者证书，做 ad-hoc 签名：Apple Silicon 要求所有代码至少有 ad-hoc 签名。
set -euo pipefail

app=$1
version=$2
arm64=$3
here=$(cd "$(dirname "$0")" && pwd)

rm -rf "$app"
mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Resources"

cp "${arm64}/Downloader" "${app}/Contents/MacOS/Downloader"
chmod +x "${app}/Contents/MacOS/Downloader"
cp "${here}/../../src/Downloader/Assets/app.icns" "${app}/Contents/Resources/app.icns"

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
  <key>CFBundleIconFile</key>
  <string>app.icns</string>
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
