# 交接：存储校验误报“存储文件有硬链接”（macOS → Windows，2026-11-06）

## 现象

macOS 启动账号 02 时弹出“账号存储隔离检查未通过”：

```
隔离检查失败：存储文件有硬链接：…/Claude-Second/ClaudeCodeConfig/file-history/ebf5f10c-…/f439aaa8fac470c6@v3
```

## 原因

不是跨账号共享。Claude Code 自己会把内容相同的检查点备份，在**同一账号的不同会话之间**做成硬链接（推测是会话分叉／续接时复制 `file-history/<会话ID>/` 用了 link）。本机账号 02 的 `file-history` 下有 68 个这样的文件，每个 inode 的两个链接都在本账号 `file-history` 内，账号外没有任何链接。

旧规则“`ClaudeCodeConfig` 内任何普通文件链接数 > 1 即未通过”因此误报。用户只能点“仍然继续”，而检查本身没有发现真正的风险。

## macOS 修复（commit `39b19cd`，dev，未发布）

- `src/main.m`：新增 `FileHistoryLinkCounts(config)`，遍历本账号 `ClaudeCodeConfig/file-history`（lstat，不跟随链接），统计链接数 > 1 的普通文件各（设备，inode）出现次数。
- `CheckStorageNode` 多一个参数 `internalLinks`：硬链接文件的链接数**等于**这个次数（全部链接都在本账号 `file-history` 内）时放行；否则仍报“存储文件有硬链接”。
- 其他位置（Cookies、Local Storage 等，以及 `ClaudeCodeConfig` 里 `file-history` 以外）规则不变。
- 只看元数据，不打开文件内容。统计和检查之间若有新链接出现，次数对不上，按未通过处理（保守）。
- 自检新增：同账号两个会话目录间的硬链接通过；再加一个指向账号外目录的链接后未通过。`--self-test` 全部 PASS（从仓库目录运行）。
- 真实账号 02 核对：所有硬链接 inode 的链接数都等于其在 `file-history` 内的出现次数，修复后不再误报。启动器 GUI 内真实启动：未执行。
- `docs/SPEC.md`「存储校验」已加“检查点硬链接例外”一段，Windows 标为待实现。

## Windows 需要做的

1. **先确认是否存在同样情况**：在 Windows 的账号 02～10 数据目录（`ClaudeCodeConfig\file-history`）里找链接数 > 1 的文件。Windows 版 Claude Code（Node `fs.link` 在 NTFS 上也能建硬链接）很可能同样会产生。没有发现也要实现，以免以后误报。
2. **实现**：`windows/src/Storage.cs` 第 101 行附近的 `if (!directory && LinkCount(path) > 1) throw …`。
   - 检查前遍历本账号 `ClaudeCodeConfig\file-history`，对每个普通文件（跳过重解析点）用已有的 `GetFileInformationByHandle` 结构取卷序列号＋文件索引（`Volume`、`IndexHigh`、`IndexLow`），只统计 `Links > 1` 的。
   - 某文件链接数等于该键的出现次数时放行；否则照旧抛出“存储项是硬链接，已停止”。
   - 只对 `ClaudeCodeConfig` 这一项做统计，其他存储项不放行。
3. **测试**（`windows/tests`，随机合成目录）：
   - `file-history\s1\f@v1` 与 `file-history\s2\f@v1` 互为硬链接 → 通过。
   - 再加一个指向账号目录外的硬链接 → 未通过。
   - `file-history` 以外（如 `Preferences`、`ClaudeCodeConfig\projects\x`）的硬链接 → 仍未通过（现有用例应继续通过）。
4. 更新 `docs/SPEC.md` 该段的 `[Windows]` 标注，写 `docs/STATUS.md`。

## 另一个未推送提交（Windows 无需改动）

`ea94488`：macOS 启动 01 前 `unsetenv("CLAUDE_CONFIG_DIR")`。Windows 已经在 `ClaudeLauncher.cs:209` 移除该变量，不受影响，仅供知悉。
