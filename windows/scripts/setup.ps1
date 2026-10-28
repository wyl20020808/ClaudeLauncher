$ErrorActionPreference = 'Stop'
# 构建启动器（含自动检查与自检），再用 Inno Setup 生成 artifacts/setup/ClaudeLauncher-Setup-<版本>.exe。
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1')
$exe = Join-Path $projectRoot 'artifacts/bin/ClaudeLauncher.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
$parts = $version.Split('.')
$display = $parts[0] + '.' + $parts[1]
$iscc = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it (for example: winget install JRSoftware.InnoSetup --scope user) and retry.' }
& $iscc /Q ('/DAppVersion=' + $display) ('/DFileVersion=' + $version) (Join-Path $projectRoot 'installer/ClaudeLauncher.iss')
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
# 未配置代码签名证书：安装程序与 exe 均未签名，客户首次运行会看到 SmartScreen 提示。
Write-Output (Join-Path $projectRoot ('artifacts/setup/ClaudeLauncher-Setup-' + $display + '.exe'))
