#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
APP="$PWD/build/Seamlet.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$PWD/build/swift-cache"
# Build separately, then replace atomically so a running receiver keeps its
# existing executable until the user quits and opens the updated app.
swiftc -swift-version 5 -O -module-cache-path "$PWD/build/swift-cache" macos/Wire.swift macos/Discovery.swift macos/FileTransfer.swift macos/EdgeBubble.swift macos/BrandUI.swift macos/main.swift -o "$PWD/build/Seamlet-next" -framework AppKit -framework Network -framework CryptoKit
mv -f "$PWD/build/Seamlet-next" "$APP/Contents/MacOS/Seamlet"
cp macos/Info.plist "$APP/Contents/Info.plist"
cp assets/brand/Seamlet.icns assets/brand/seamlet-icon.png "$APP/Contents/Resources/"
codesign --force --sign - "$APP"
echo "Built $APP"
