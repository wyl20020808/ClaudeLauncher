# 同步会话只读诊断：统计各账号每个清单项目的会话记录（未归档／已归档／缺正文）和没有记录的 JSONL。
# 只读文件名和记录里的 cliSessionId、cwd、isArchived，不输出标题、正文、凭据，不写任何文件，不需要关闭 Claude。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File diagnose-session-sync.ps1 > 诊断.txt
param([string]$Exe = '')  # 不填时找已安装的启动器
$ErrorActionPreference = 'Continue'
$guid = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
function Slug([string]$p) { -join ($p.ToCharArray() | ForEach-Object { if ($_ -match '[A-Za-z0-9]') { $_ } else { '-' } }) }
function Norm([string]$p) { if (-not $p) { return $null }; return ($p -replace '/', '\').TrimEnd('\').ToLowerInvariant() }
function Within([string]$path, [string]$root) { $a = Norm $path; $b = Norm $root; return $a -and ($a -eq $b -or $a.StartsWith($b + '\')) }

$listFile = Join-Path $env:LOCALAPPDATA 'Claude-MultiLauncher\projects.json'
$projects = @()
try { $projects = @(([IO.File]::ReadAllText($listFile) | ConvertFrom-Json).projects | ForEach-Object { $_.path }) } catch { Write-Output "无法读取项目清单 $listFile ：$($_.Exception.Message)" }
Write-Output "启动器项目清单（$($projects.Count) 个）："
$projects | ForEach-Object { Write-Output ('  ' + $_ + $(if (Test-Path -LiteralPath $_) { '' } else { '   [目录不存在，同步窗口不显示]' })) }
Add-Type -AssemblyName System.Web.Extensions
$serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer; $serializer.MaxJsonLength = [int]::MaxValue; $serializer.RecursionLimit = 1000
# 已安装的启动器：用它自己的列表逻辑跑一遍（只读）。
$exe = @($Exe, "$env:ProgramFiles\Claude 多开启动器\ClaudeLauncher.exe", "${env:ProgramFiles(x86)}\Claude 多开启动器\ClaudeLauncher.exe", "$env:LOCALAPPDATA\Programs\Claude 多开启动器\ClaudeLauncher.exe") | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
$list = $null
if ($exe) {
    try {
        $assembly = [Reflection.Assembly]::LoadFrom($exe)
        Write-Output ('已安装启动器：' + $exe + '  版本 ' + $assembly.GetType('ClaudeLauncher.Updates').GetField('DisplayVersion').GetValue($null))
        $list = $assembly.GetType('ClaudeLauncher.SessionSync').GetMethod('List', [Reflection.BindingFlags]'Static,NonPublic')
        if ($list.GetParameters().Count -ne 3) { Write-Output '  （这是 1.8.6 或更早版本，跳过新逻辑对比）'; $list = $null }
    } catch { Write-Output ('无法加载已安装启动器：' + $_.Exception.Message) }
} else { Write-Output '没有找到已安装的启动器，跳过新逻辑对比。' }
$local = $env:LOCALAPPDATA; $roaming = $env:APPDATA; $package = Join-Path $local 'Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming'

foreach ($n in 1..10) {
    $name = if ($n -eq 1) { 'Claude' } elseif ($n -eq 2) { 'Claude-Second' } else { 'Claude-Profile-' + $n.ToString('00') }
    $profiles = @((Join-Path $roaming $name), (Join-Path $package $name))
    if ($n -eq 1) { $profiles += Join-Path $local 'Claude-Data\claude_pzs8sxrjxfjjc'; $config = Join-Path $env:USERPROFILE '.claude' }
    $profiles = @($profiles | Where-Object { Test-Path -LiteralPath $_ })
    if ($profiles.Count -eq 0) { continue }
    if ($n -ne 1) { $configs = @($profiles | ForEach-Object { Join-Path $_ 'ClaudeCodeConfig' } | Where-Object { Test-Path -LiteralPath $_ }); $config = if ($configs.Count -ge 1) { $configs[0] } else { $null } }
    Write-Output ''
    Write-Output ('===== 账号 ' + $n.ToString('00') + ' =====')
    Write-Output ('配置目录：' + $(if ($config) { $config } else { '没有（同步窗口里这个账号没有会话）' }))
    $records = @(); $strictFailed = 0
    $real = @{}
    if ($list -and $config) {
        # 参数须是原生 .NET 类型，New-Object 和管道的结果会被包成 PSObject。
        $arguments = New-Object object[] 3; $arguments[0] = [string]$config; $arguments[1] = [string[]]$profiles; $arguments[2] = [System.Collections.Generic.List[string]]::new([string[]]$projects)
        try { foreach ($row in $list.Invoke($null, $arguments)) { $real[$row.ProjectPath] = 1 + [int]$real[$row.ProjectPath] } }
        catch { Write-Output ('  新逻辑运行出错：' + $_.Exception.InnerException.Message) }
    }
    foreach ($p in $profiles) {
        $root = Join-Path $p 'claude-code-sessions'
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Write-Output ('记录目录：' + $root)
        # 启动器要求这两层只有 GUID 名的目录，否则整层跳过。
        foreach ($level in @(Get-Item -LiteralPath $root) + @(Get-ChildItem -LiteralPath $root -Directory -Force)) {
            $odd = @(Get-ChildItem -LiteralPath $level.FullName -Force | Where-Object { ($_.Name -notmatch $guid -and $_.PSIsContainer) -or ($_.Name -match $guid -and -not $_.PSIsContainer) -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })
            if ($odd.Count) { Write-Output ('  [不安全，启动器会跳过这一层] ' + $level.FullName + ' 含：' + (($odd | ForEach-Object { $_.Name }) -join ', ')) }
        }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter 'local_*.json' -File -Force -ErrorAction SilentlyContinue) {
            if ($file.Directory.Parent.Parent.FullName -ne (Get-Item -LiteralPath $root).FullName) { continue }
            try { $bytes = [IO.File]::ReadAllBytes($file.FullName); $text = [Text.Encoding]::UTF8.GetString($bytes) } catch { Write-Output ('  [打不开] ' + $file.Name); continue }
            # 与启动器相同的严格解析：非法 UTF-8 或 JSON 解析失败时启动器跳过这条。
            try { [void]$serializer.DeserializeObject((New-Object Text.UTF8Encoding($false, $true)).GetString($bytes)) } catch { $strictFailed++; Write-Output ('  [启动器解析失败，会漏掉] ' + $file.Name + '：' + $_.Exception.InnerException.GetType().Name) }
            $id = [regex]::Match($text, '"cliSessionId"\s*:\s*"([^"]+)"').Groups[1].Value
            $cwd = [regex]::Match($text, '"cwd"\s*:\s*"((?:[^"\\]|\\.)*)"').Groups[1].Value -replace '\\\\', '\'
            $archived = [regex]::Match($text, '"isArchived"\s*:\s*(true|false)').Groups[1].Value -eq 'true'
            if ($file.Length -gt 8MB) { Write-Output ('  [超过 8 MiB，启动器跳过] ' + $file.Name) }
            $jsonl = if ($config -and $id -and $cwd) { Join-Path $config ('projects\' + (Slug $cwd) + '\' + $id + '.jsonl') } else { $null }
            $records += [pscustomobject]@{ Id = $id.ToLowerInvariant(); Cwd = $cwd; Archived = $archived; HasJsonl = [bool]($jsonl -and (Test-Path -LiteralPath $jsonl)) }
        }
    }
    $active = @($records | Where-Object { -not $_.Archived })
    Write-Output ('记录：共 ' + $records.Count + ' 条，未归档 ' + $active.Count + ' 条，已归档 ' + ($records.Count - $active.Count) + ' 条，启动器解析失败 ' + $strictFailed + ' 条')
    Write-Output '按清单项目（同步窗口只列“未归档且有正文”的）：'
    foreach ($proj in $projects) {
        $in = @($records | Where-Object { Within $_.Cwd $proj })
        $ok = @($in | Where-Object { -not $_.Archived -and $_.HasJsonl }).Count
        $arch = @($in | Where-Object { $_.Archived }).Count
        $missing = @($in | Where-Object { -not $_.Archived -and -not $_.HasJsonl }).Count
        $orphans = 0; $old = 0
        if ($config) { $dir = Join-Path $config ('projects\' + (Slug $proj)); if (Test-Path -LiteralPath $dir) { $files = @(Get-ChildItem -LiteralPath $dir -Filter '*.jsonl' -File | Where-Object { $_.Length -gt 0 }); $old = $files.Count; $known = @($records | ForEach-Object { $_.Id }); $orphans = @($files | Where-Object { $known -notcontains $_.BaseName.ToLowerInvariant() }).Count } }
        $now = if ($list) { '，1.8.7 实际列出 ' + [int]$real[$proj] } else { '' }
        Write-Output ('  ' + $proj + '：1.8.6 会列出 ' + $old + $now + '；按记录：可同步 ' + $ok + '，已归档 ' + $arch + '，未归档但缺正文 ' + $missing + '，没有记录的 JSONL ' + $orphans)
    }
    $outside = @($active | Where-Object { $c = $_.Cwd; -not ($projects | Where-Object { Within $c $_ }) } | Group-Object { (Norm $_.Cwd) -replace '\\scratch-workspaces\\.*$', '\scratch-workspaces\…（临时工作区）' })
    if ($outside.Count) {
        Write-Output '未归档、但不在清单项目里的会话（同步窗口不显示）：'
        $outside | Sort-Object Count -Descending | ForEach-Object { Write-Output ('  ' + $_.Count + ' 条  ' + $_.Name) }
    }
}
