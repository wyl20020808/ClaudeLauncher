#!/bin/zsh
set -euo pipefail
cd "${0:A:h}/.."
APP="${1:-build/Claude 启动器.app}"
ZIP="${2:-build/Claude启动器.zip}"
[[ -d "$APP/Contents" ]] || { print -u2 '未找到已构建应用，请先运行 scripts/build.sh'; exit 1; }
/usr/bin/codesign --verify --strict "$APP"
mkdir -p "${ZIP:h}"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
/usr/bin/unzip -tq "$ZIP"
print "已打包：${ZIP:A}"
