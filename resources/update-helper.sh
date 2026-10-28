#!/bin/zsh
# 自动更新替换脚本：等启动器退出后，用已校验的新版替换旧版；失败则恢复旧版。
# 参数：启动器 PID、当前应用路径、已校验的新版路径、旧版备份路径（三者同目录，均以 .app 结尾）。
# 只移动这三个应用包，不触碰账号数据或任何 Claude 实例。
pid=$1 current=$2 staged=$3 backup=$4
for p in "$current" "$staged" "$backup"; do
    [[ "$p" == *.app && "${p:h}" == "${current:h}" ]] || exit 2
done
for i in {1..150}; do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
if kill -0 "$pid" 2>/dev/null; then rm -rf -- "$staged"; exit 3; fi
if ! mv -- "$current" "$backup"; then rm -rf -- "$staged"; open -- "$current"; exit 4; fi
if mv -- "$staged" "$current" && /usr/bin/codesign --verify --strict -- "$current"; then
    open -- "$current"; exit 0
fi
rm -rf -- "$current" "$staged"
mv -- "$backup" "$current"
open -- "$current"
exit 5
