#!/bin/zsh
# 替身 curl / revert 不访问网络，也不调用发布脚本。
set -euo pipefail
source "${0:A:h}/release-verification.sh"
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/launcher-release-test.XXXXXX")
trap 'rm -rf -- "$TEST_ROOT"' EXIT
print 'synthetic signed asset' > "$TEST_ROOT/expected"
curl() {
    [[ "$TEST_MODE" == timeout ]] && return 28
    if [[ "$TEST_MODE" == correct ]]; then cp "$TEST_ROOT/expected" "$4"; else print corrupted > "$4"; fi
}
revert() { print -r -- "$1" >> "$TEST_ROOT/reverts"; return 1; }
for asset in latest-windows.json DMG Setup; do
    comparison=sha256
    expected=$(shasum -a 256 "$TEST_ROOT/expected" | cut -d' ' -f1)
    if [[ "$asset" == latest-windows.json ]]; then comparison=contents; expected="$TEST_ROOT/expected"; fi
    for TEST_MODE in timeout correct incorrect; do
        : > "$TEST_ROOT/reverts"
        result=0
        verify_published_asset "https://fixture.invalid/$asset" "$TEST_ROOT/download" "$expected" "$comparison" "$asset" > "$TEST_ROOT/output" 2>&1 || result=$?
        case "$TEST_MODE" in
            timeout) [[ "$result" -eq 2 && ! -s "$TEST_ROOT/reverts" ]]; /usr/bin/grep -q '待复核' "$TEST_ROOT/output" ;;
            correct) [[ "$result" -eq 0 && ! -s "$TEST_ROOT/reverts" ]] ;;
            incorrect) [[ "$result" -eq 1 && $(wc -l < "$TEST_ROOT/reverts") -eq 1 ]] ;;
        esac
        print "PASS: release verification $asset $TEST_MODE (exit $result)"
    done
done
