#!/bin/zsh
# 把已构建的应用做成拖拽安装的 DMG（应用 + “应用程序”快捷方式）。不重新构建、不修改应用。
set -euo pipefail
cd "${0:A:h}/.."
APP="${1:-build/Claude 启动器.app}"
[[ -d "$APP/Contents" ]] || { print -u2 '未找到已构建应用，请先运行 scripts/build.sh'; exit 1; }
VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")
# 文件名只用 ASCII：GitHub 会把附件名中的非 ASCII 字符替换掉（实测 Claude多开启动器 → Claude.）。
DMG="${2:-build/ClaudeLauncher-$VERSION.dmg}"
/usr/bin/codesign --verify --strict "$APP"
STAGE=$(mktemp -d "${TMPDIR:-/tmp}/claude-launcher-dmg.XXXXXX")
trap 'rm -rf "$STAGE"' EXIT
/usr/bin/ditto "$APP" "$STAGE/${APP:t}"
ln -s /Applications "$STAGE/应用程序"
mkdir -p "${DMG:h}"
/usr/bin/hdiutil create -volname "Claude 多开启动器 $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
# 可选：用同一证书给 DMG 签名。自签名证书不能替代 Apple 公证。
[[ -n "${SIGN_IDENTITY:-}" ]] && /usr/bin/codesign --force --sign "$SIGN_IDENTITY" "$DMG"
/usr/bin/hdiutil verify "$DMG" >/dev/null
print "已生成：${DMG:A}"
# 发布机签名清单所需：文件名、字节数、SHA-256。
print "file=${DMG:t}"
print "size=$(stat -f %z "$DMG")"
print "sha256=$(shasum -a 256 "$DMG" | cut -d" " -f1)"
