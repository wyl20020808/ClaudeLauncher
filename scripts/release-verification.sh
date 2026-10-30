#!/bin/zsh
# 只定义发布后的附件复核；调用方提供 curl 和 revert，便于完全离线测试。
verify_published_asset() {
    local url=$1 downloaded=$2 expected=$3 comparison=$4 description=$5
    if ! curl -fsSL "$url" -o "$downloaded"; then
        print -u2 "待复核：$description 下载失败，保留已发布版本，请稍后重试。"
        return 2
    fi
    if [[ "$comparison" == contents ]]; then
        cmp -s "$downloaded" "$expected" && return 0
    elif [[ "$comparison" == sha256 ]]; then
        [[ "$(shasum -a 256 "$downloaded" | cut -d' ' -f1)" == "$expected" ]] && return 0
    else
        print -u2 '未知的附件复核方式'; return 2
    fi
    revert "$description 内容不一致"
    return 1
}
