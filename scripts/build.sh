#!/bin/zsh
set -euo pipefail
cd "${0:A:h}/.."
OUT="${1:-build}"
mkdir -p "$OUT"
OUT="${OUT:A}"
APP="$OUT/Claude 启动器.app"
# 每次从空目录生成，避免旧资源（如以前复制的官方图标）残留；拒绝直接写入“应用程序”文件夹。
[[ "$APP" != /Applications/* ]] || { print -u2 "不要直接构建到 /Applications，请用 DMG 安装"; exit 1; }
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
BUILD_SDK="${SDKROOT:-}"
if [[ -z "$BUILD_SDK" ]]; then
    if [[ -d /Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk ]]; then
        BUILD_SDK=/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk
    else
        BUILD_SDK=$(xcrun --show-sdk-path)
    fi
fi
xcrun clang -isysroot "$BUILD_SDK" -arch arm64 -arch x86_64 -fobjc-arc -Wall -Wextra -Wno-unused-parameter -mmacosx-version-min=12.0 -framework Cocoa -framework ApplicationServices -framework Security -framework ServiceManagement -framework SystemConfiguration -framework UniformTypeIdentifiers -framework Carbon -lsqlite3 src/main.m src/Projects.m src/ProjectImport.m src/SessionSync.m src/UsageInterface.m src/Settings.m src/ProxyCheck.m src/Handoff.m src/Localization.m src/ConfigSync.m src/UsageTrend.m -o "$APP/Contents/MacOS/ClaudeLauncher"
cp resources/icon/AppIcon.icns resources/icon/menubar.png resources/i18n/english.strings "$APP/Contents/Resources/"
cp resources/tutorial/* resources/ui/*.webp resources/update-helper.sh windows/assets/update-public-key.xml "$APP/Contents/Resources/"
# Mac 发布公钥在包内改名：NSBundle 会把以 -macos 结尾的文件当作平台变体，顶替同名的共用公钥。
cp resources/update-public-key-macos.xml "$APP/Contents/Resources/macos-publisher-key.xml"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>ClaudeLauncher</string>
<key>CFBundleIdentifier</key><string>local.claude.profile-launcher</string>
<key>CFBundleName</key><string>Claude 启动器</string>
<key>CFBundleDisplayName</key><string>Claude 启动器</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>27</string>
<key>CFBundleShortVersionString</key><string>1.7.20</string>
<key>CFBundleIconFile</key><string>AppIcon.icns</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
# 运行时更新来源固定为本项目 GitHub Releases，不再向包内注入任意自建地址。
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
