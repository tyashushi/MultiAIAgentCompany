#!/bin/bash
# macOS の .app バンドルを作る（設計 §43）。
#
# **なぜ要るか**: `dotnet run` で動かしている限り、Dock に出るのは dotnet の汎用アイコンで、
# アプリのアイコンは**どこにも出ない**。アイコンを実物で確かめるには、この形が要る。
#
# 使い方: spikes/app-bundle/make-app.sh [出力先ディレクトリ]
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-$repo/spikes/app-bundle/out}"
app="$out/MultiAIAgentCompany.app"
icns="$repo/spikes/app-bundle/AppIcon.icns"

if [ ! -f "$icns" ]; then
  echo "AppIcon.icns がありません: $icns" >&2
  echo "  spikes/icon-slicer で iconset を作り、iconutil -c icns で変換してここへ置く" >&2
  exit 1
fi

echo "== publish =="
rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish "$repo/src/MultiAIAgentCompany.Desktop/MultiAIAgentCompany.Desktop.csproj" \
  -c Release -o "$app/Contents/MacOS" --nologo

cp "$icns" "$app/Contents/Resources/AppIcon.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>MultiAIAgentCompany</string>
    <key>CFBundleDisplayName</key>
    <string>MultiAIAgentCompany</string>
    <key>CFBundleIdentifier</key>
    <string>com.example.multiaiagentcompany</string>
    <key>CFBundleVersion</key>
    <string>0.1</string>
    <key>CFBundleShortVersionString</key>
    <string>0.1</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>MultiAIAgentCompany.Desktop</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
</dict>
</plist>
PLIST

# **Finder のアイコン取得は結果をためる。** 作り直しても古いアイコンが出ることがあるので、
# バンドルの更新時刻を今にして、キャッシュを外す。
touch "$app"

echo "== できた =="
echo "$app"
echo "open \"$app\" で起動する（Dock にアイコンが出る）"
