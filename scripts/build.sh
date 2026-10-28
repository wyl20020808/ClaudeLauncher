#!/bin/zsh
set -euo pipefail
cd "${0:A:h}/.."
OUT="${1:-build}"
mkdir -p "$OUT"
OUT="${OUT:A}"
APP="$OUT/Claude 启动器.app"
CLAUDE=/Applications/Claude.app
[[ -d "$CLAUDE" ]] || CLAUDE="$HOME/Applications/Claude.app"
[[ -d "$CLAUDE" ]] || { print -u2 '未找到官方 Claude.app'; exit 1; }
ICON=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$CLAUDE/Contents/Info.plist")
[[ "$ICON" == *.icns ]] || ICON="$ICON.icns"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
BUILD_SDK="${SDKROOT:-}"
if [[ -z "$BUILD_SDK" ]]; then
    if [[ -d /Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk ]]; then
        BUILD_SDK=/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk
    else
        BUILD_SDK=$(xcrun --show-sdk-path)
    fi
fi
xcrun clang -isysroot "$BUILD_SDK" -fobjc-arc -Wall -Wextra -Wno-unused-parameter -mmacosx-version-min=12.0 -framework Cocoa -framework ApplicationServices src/main.m -o "$APP/Contents/MacOS/ClaudeLauncher"
cp "$CLAUDE/Contents/Resources/$ICON" "$APP/Contents/Resources/Claude.icns"
cp resources/ui/*.webp "$APP/Contents/Resources/"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>ClaudeLauncher</string>
<key>CFBundleIdentifier</key><string>local.claude.profile-launcher</string>
<key>CFBundleName</key><string>Claude 启动器</string>
<key>CFBundleDisplayName</key><string>Claude 启动器</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>7</string>
<key>CFBundleShortVersionString</key><string>1.6</string>
<key>CFBundleIconFile</key><string>Claude.icns</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
# SIGN_IDENTITY 为钥匙串中的代码签名证书名；留空用 ad-hoc（每次重建身份都会变，辅助功能授权失效）。
SIGN="${SIGN_IDENTITY:--}"
KEYCHAIN_ARGS=()
[[ -n "${SIGN_KEYCHAIN:-}" ]] && KEYCHAIN_ARGS=(--keychain "$SIGN_KEYCHAIN")
/usr/bin/codesign --force --sign "$SIGN" "${KEYCHAIN_ARGS[@]}" "$APP"
[[ "$SIGN" == - ]] && print -u2 '提示：未设置 SIGN_IDENTITY，使用 ad-hoc 签名，重建后需重新授权辅助功能。'
/usr/bin/codesign -d -r- "$APP" 2>&1 | grep designated
/usr/bin/plutil -lint "$APP/Contents/Info.plist"
/usr/bin/codesign --verify --strict "$APP"
"$APP/Contents/MacOS/ClaudeLauncher" --self-test
print "已构建：$APP"
