#!/bin/zsh
# macOS 独立发布（SPEC“更新与安装”）：构建 DMG → 用钥匙串中的 Mac 私钥签名 latest-macos.json 并验签 →
# 从当前最新 Release 带入已签名的 Windows 清单与 Setup（共用公钥验签、核对大小与 SHA-256，不重签）→
# 草稿 macos-v<版本> 上传四个文件 → 经 API 下载草稿逐字节核对 → 转正式并设为 Latest →
# 等待 latest/download 缓存更新（最多 10 分钟）后复核两端。内容不符才回滚：改回草稿并把上一版设回 Latest；
# 只是缓存未及时更新则保留发布（附件已核对），提示稍后复核并以状态 2 退出。
# 自建服务由 Windows 端定时同步，这里不操作。私钥不导出、不打印。
#   用法：scripts/publish-macos.sh [--dry-run]     --dry-run 只构建、签名、本地校验与带入核对，不创建 Release
set -euo pipefail
cd "${0:A:h}/.."
REPO=wyl20020808/ClaudeLauncher
DOWNLOAD="https://github.com/$REPO/releases/latest/download"
SIGN_IDENTITY="${SIGN_IDENTITY:-ClaudeLauncher Local Signing}"
DRY=0; [[ "${1:-}" == --dry-run ]] && DRY=1
SDK=/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk; [[ -d $SDK ]] || SDK=$(xcrun --show-sdk-path)
step() { print "== $1"; }
die() { print -u2 "失败：$1"; exit 1; }
field() { /usr/bin/plutil -extract "$2" raw -o - "$1"; }

step '检查仓库与工具'
[[ -z "$(git status --porcelain)" ]] || die '工作区有未提交改动'
HEAD=$(git rev-parse HEAD)
(( DRY )) || git branch -r --contains "$HEAD" | grep -q . || die '当前提交还没推送到远端'
(( DRY )) || gh auth status >/dev/null 2>&1 || die 'gh 未登录'
mkdir -p build
TOOL=build/release-key
if [[ ! -x $TOOL || scripts/release-key.m -nt $TOOL ]]; then
    xcrun clang -isysroot "$SDK" -fobjc-arc -framework Foundation -framework Security scripts/release-key.m -o $TOOL
    # 固定证书签名，钥匙串访问权限按签名身份识别，重建后不必重新授权。
    codesign --force --sign "$SIGN_IDENTITY" --identifier local.claude.profile-launcher.release-key $TOOL
fi
[[ "$($TOOL public)" == "$(tr -d '\n' < resources/update-public-key-macos.xml)" ]] || die '钥匙串中的 Mac 密钥与 resources/update-public-key-macos.xml 不一致'

WORK=$(mktemp -d "${TMPDIR:-/tmp}/claude-launcher-publish.XXXXXX")
trap 'rm -rf "$WORK"' EXIT

step '构建并打包'
SIGN_IDENTITY="$SIGN_IDENTITY" /bin/zsh scripts/build.sh >/dev/null
APP="build/Claude 启动器.app"
VERSION=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")
[[ "$VERSION" =~ '^[0-9]+\.[0-9]+\.[0-9]+$' ]] || die "版本号格式不符：$VERSION"
SIGN_IDENTITY="$SIGN_IDENTITY" /bin/zsh scripts/dmg.sh >/dev/null
DMG="build/ClaudeLauncher-$VERSION.dmg"
SIZE=$(stat -f %z "$DMG"); SHA=$(shasum -a 256 "$DMG" | cut -d' ' -f1)

step '核对当前已发布版本（只升不降）'
curl -fsSL "$DOWNLOAD/latest-macos.json" -o "$WORK/current-macos.json" || die '无法读取当前 latest-macos.json'
CURRENT=$(field "$WORK/current-macos.json" version)
autoload -Uz is-at-least
is-at-least "$VERSION" "$CURRENT" && die "新版本 $VERSION 不高于已发布的 $CURRENT"

step "签名 latest-macos.json（$VERSION）"
printf 'macos\n%s\n%s\n%s\n%s\n' "$VERSION" "${DMG:t}" "$SIZE" "$SHA" > "$WORK/payload-macos"
SIG=$($TOOL sign "$WORK/payload-macos")
$TOOL verify resources/update-public-key-macos.xml "$WORK/payload-macos" "$SIG" >/dev/null || die 'Mac 公钥验签失败'
printf '{"platform":"macos","version":"%s","file":"%s","size":%s,"sha256":"%s","minimumOS":"12.0","signature":"%s"}\n' "$VERSION" "${DMG:t}" "$SIZE" "$SHA" "$SIG" > "$WORK/latest-macos.json"

step '带入 Windows 清单与 Setup（不重签）'
curl -fsSL "$DOWNLOAD/latest-windows.json" -o "$WORK/latest-windows.json" || die '无法读取 latest-windows.json'
WFILE=$(field "$WORK/latest-windows.json" file); WSIZE=$(field "$WORK/latest-windows.json" size); WSHA=$(field "$WORK/latest-windows.json" sha256)
[[ "$(field "$WORK/latest-windows.json" platform)" == windows && "$WFILE" == ClaudeLauncherSetup-*.exe ]] || die 'Windows 清单字段不符'
printf 'windows\n%s\n%s\n%s\n%s\n' "$(field "$WORK/latest-windows.json" version)" "$WFILE" "$WSIZE" "$WSHA" > "$WORK/payload-windows"
$TOOL verify windows/assets/update-public-key.xml "$WORK/payload-windows" "$(field "$WORK/latest-windows.json" signature)" >/dev/null || die 'Windows 清单共用公钥验签失败'
curl -fsSL "$DOWNLOAD/$WFILE" -o "$WORK/$WFILE" || die "无法下载 $WFILE"
[[ "$(stat -f %z "$WORK/$WFILE")" == "$WSIZE" && "$(shasum -a 256 "$WORK/$WFILE" | cut -d' ' -f1)" == "$WSHA" ]] || die 'Windows Setup 大小或 SHA-256 不符'

print "file=${DMG:t}\nsize=$SIZE\nsha256=$SHA\nwindows=$WFILE"
if (( DRY )); then step 'dry-run：本地校验全部通过，未创建 Release'; exit 0; fi

TAG="macos-v$VERSION"
step "创建草稿 $TAG 并上传四个文件"
gh release create "$TAG" --repo $REPO --draft --target "$HEAD" --title "macOS $VERSION" --notes "macOS $VERSION（Windows 文件沿用 $WFILE）" \
    "$DMG" "$WORK/latest-macos.json" "$WORK/latest-windows.json" "$WORK/$WFILE" >/dev/null
ASSETS=$(gh release view "$TAG" --repo $REPO --json assets --jq '[.assets[].name] | sort | join(",")')
[[ "$ASSETS" == "$(print -l "${DMG:t}" "$WFILE" latest-macos.json latest-windows.json | sort | paste -sd, -)" ]] || die "草稿附件不全：$ASSETS（草稿保留，未发布）"

step '经 API 下载草稿附件核对（不经 latest/download 缓存）'
mkdir "$WORK/draft"
gh release download "$TAG" --repo $REPO -D "$WORK/draft" || die '无法下载草稿附件（草稿保留，未发布）'
for f in latest-macos.json latest-windows.json; do cmp -s "$WORK/draft/$f" "$WORK/$f" || die "草稿 $f 与本地不一致（草稿保留，未发布）"; done
[[ "$(shasum -a 256 "$WORK/draft/${DMG:t}" | cut -d' ' -f1)" == "$SHA" ]] || die '草稿 DMG 哈希不一致（草稿保留，未发布）'
[[ "$(shasum -a 256 "$WORK/draft/$WFILE" | cut -d' ' -f1)" == "$WSHA" ]] || die '草稿 Windows Setup 哈希不一致（草稿保留，未发布）'

step '转为正式并设为 Latest'
PREV=$(gh api "repos/$REPO/releases/latest" --jq .tag_name)
gh release edit "$TAG" --repo $REPO --draft=false --latest >/dev/null
# 只在确认内容不符时回滚；显式恢复上一版 Latest，避免 GitHub 自动选到更早的版本导致 latest/download 失效。
revert() {
    gh release edit "$TAG" --repo $REPO --draft=true >/dev/null 2>&1 || true
    gh release edit "$PREV" --repo $REPO --latest >/dev/null 2>&1 || true
    die "$1（已改回草稿，$PREV 恢复为 Latest）"
}

step '等待 latest/download 更新（最多 10 分钟）并复核'
SERVED=''
for attempt in {1..40}; do
    curl -fsSL "$DOWNLOAD/latest-macos.json" -o "$WORK/check-macos.json" 2>/dev/null && SERVED=$(field "$WORK/check-macos.json" version 2>/dev/null) && [[ "$SERVED" == "$VERSION" ]] && break
    sleep 15
done
if [[ "$SERVED" != "$VERSION" ]]; then
    print -u2 "提醒：已发布 $TAG（附件已经 API 核对），但 latest/download 10 分钟内仍返回 ${SERVED:-无法读取}。属于缓存未更新，未回滚；请稍后重新复核。"
    exit 2
fi
cmp -s "$WORK/check-macos.json" "$WORK/latest-macos.json" || revert 'latest-macos.json 与本地签名不一致'
curl -fsSL "$DOWNLOAD/latest-windows.json" -o "$WORK/check-windows.json" && cmp -s "$WORK/check-windows.json" "$WORK/latest-windows.json" || revert 'latest-windows.json 不一致'
curl -fsSL "$DOWNLOAD/${DMG:t}" -o "$WORK/check.dmg" && [[ "$(shasum -a 256 "$WORK/check.dmg" | cut -d' ' -f1)" == "$SHA" ]] || revert 'DMG 哈希不一致'
curl -fsSL "$DOWNLOAD/$WFILE" -o "$WORK/check.exe" && [[ "$(shasum -a 256 "$WORK/check.exe" | cut -d' ' -f1)" == "$WSHA" ]] || revert 'Windows Setup 哈希不一致'
step "已发布：https://github.com/$REPO/releases/tag/$TAG"
