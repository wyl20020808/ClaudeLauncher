#!/bin/zsh
# 只在随机临时目录中运行安装辅助脚本；open 始终是替身，不打开任何应用。
set -euo pipefail
REPO=${0:A:h:h}
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/launcher-helper-test.XXXXXX")
trap 'rm -rf -- "$TEST_ROOT"' EXIT
mkdir -p "$TEST_ROOT/bin" "$TEST_ROOT/template.app/Contents/MacOS"
cp /usr/bin/true "$TEST_ROOT/template.app/Contents/MacOS/Fixture"
cat > "$TEST_ROOT/template.app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict><key>CFBundleIdentifier</key><string>local.claude.helper-fixture</string><key>CFBundleExecutable</key><string>Fixture</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>
PLIST
/usr/bin/codesign --force --sign - "$TEST_ROOT/template.app" >/dev/null 2>&1
cat > "$TEST_ROOT/bin/open" <<'SH'
#!/bin/sh
printf '%s\n' "$*" >> "$TEST_CASE/open-calls"
if [ "$TEST_MODE" = open-failure ] && [ ! -f "$TEST_CASE/open-failed" ]; then
    touch "$TEST_CASE/open-failed"
    exit 7
fi
exit 0
SH
cat > "$TEST_ROOT/bin/mv" <<'SH'
#!/bin/sh
if [ "$TEST_MODE" = replace-failure ] && [ "$2" = "$TEST_CASE/staged.app" ]; then exit 1; fi
exec /bin/mv "$@"
SH
chmod +x "$TEST_ROOT/bin/open" "$TEST_ROOT/bin/mv"
# 使用已退出的合成子进程 PID，不向真实应用发送信号。
/usr/bin/true &
FINISHED_PID=$!
wait "$FINISHED_PID"
for TEST_MODE in replace-failure signature-failure open-failure success; do
    TEST_CASE="$TEST_ROOT/$TEST_MODE"
    export TEST_CASE TEST_MODE
    mkdir -p "$TEST_CASE/current.app"
    print old > "$TEST_CASE/current.app/old-marker"
    /usr/bin/ditto "$TEST_ROOT/template.app" "$TEST_CASE/staged.app"
    if [[ "$TEST_MODE" == signature-failure ]]; then print tampered >> "$TEST_CASE/staged.app/Contents/MacOS/Fixture"; fi
    result=0
    PATH="$TEST_ROOT/bin:$PATH" /bin/zsh "$REPO/resources/update-helper.sh" "$FINISHED_PID" \
        "$TEST_CASE/current.app" "$TEST_CASE/staged.app" "$TEST_CASE/backup.app" > "$TEST_CASE/output" 2>&1 || result=$?
    expected=5; [[ "$TEST_MODE" == open-failure ]] && expected=6; [[ "$TEST_MODE" == success ]] && expected=0
    [[ "$result" == "$expected" ]] || { cat "$TEST_CASE/output"; print -u2 "FAIL: $TEST_MODE exit=$result expected=$expected"; exit 1; }
    [[ ! -e "$TEST_CASE/staged.app" ]]
    calls=$(wc -l < "$TEST_CASE/open-calls")
    if [[ "$TEST_MODE" == success ]]; then
        [[ -f "$TEST_CASE/current.app/Contents/MacOS/Fixture" && -f "$TEST_CASE/backup.app/old-marker" && "$calls" -eq 1 ]]
    else
        [[ -f "$TEST_CASE/current.app/old-marker" && ! -e "$TEST_CASE/backup.app" ]]
        expected_calls=1; [[ "$TEST_MODE" == open-failure ]] && expected_calls=2
        [[ "$calls" -eq "$expected_calls" ]]
    fi
    print "PASS: update-helper $TEST_MODE (exit $result, open calls $calls)"
done
