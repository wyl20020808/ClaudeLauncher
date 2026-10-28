param(
    [long]$MacSize = 0,                                  # 同时发布 macOS 时：dmg.sh 给出的 size
    [string]$MacSha256 = '',                             # 同时发布 macOS 时：dmg.sh 给出的 sha256
    [string]$MacDmg = '',                                # 本地 DMG；为空时从草稿 Release 下载
    [string]$KeyFile = (Join-Path $env:LOCALAPPDATA 'ClaudeLauncher-Publisher/signing-key.dpapi'),
    [string]$PublicDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'updates/public'),
    [string]$Repository = 'wyl20020808/ClaudeLauncher',
    [switch]$DryRun                                      # 只构建、签名、本地校验，不上传；允许测试密钥
)
# SPEC 更新与安装 · 发布流程。两种方式：
# - 两端同时发布（给出 -MacSize/-MacSha256）：标签 v<版本>，macOS 先把 DMG 传到同名草稿 Release。
# - 只发布 Windows（不给 macOS 参数）：标签 windows-v<版本>，从当前最新 Release 取出 latest-macos.json 与对应 DMG，
#   验签核对后一并放进新 Release，保证 GitHub latest/download 回退对 macOS 仍然有效；自建服务的 macOS 清单不动。
# 任一步失败都不留下半成品：正式发布之后的失败会恢复自建清单并把 Release 改回草稿。私钥只在内存中使用，不落盘、不输出。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Web.Extensions
$projectRoot = Split-Path $PSScriptRoot -Parent
function Step([string]$text) { Write-Output ('== ' + $text) }
# PowerShell 函数名不区分大小写：必须直接调用 gh.exe，否则包装函数会递归调用自身。
$ghExe = $null
$ghCommand = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ghCommand) { $ghExe = $ghCommand.Source }
function Invoke-Gh {
    if (-not $ghExe) { throw 'GitHub CLI (gh) not found.' }
    & $ghExe @args
    if ($LASTEXITCODE -ne 0) { throw ('gh ' + ($args -join ' ') + ' failed.') }
}
function Write-Utf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }

$joint = $MacSize -gt 0 -or $MacSha256 -or $MacDmg
if ($joint -and ($MacSize -le 0 -or $MacSha256 -notmatch '^[a-f0-9]{64}$')) { throw 'Joint release needs -MacSize and a 64-character lowercase -MacSha256.' }
if (-not $ghExe) { throw 'GitHub CLI (gh) not found.' }
if (-not $DryRun) {
    & $ghExe auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in. Run: gh auth login' }
}
# 在 Codex 桌面版（打包应用）中运行 init-update-key.ps1 时，%LOCALAPPDATA% 写入被重定向到其包缓存；同一账户下 DPAPI 照常解密。
if (-not $PSBoundParameters.ContainsKey('KeyFile') -and -not (Test-Path -LiteralPath $KeyFile)) {
    $redirected = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'OpenAI.Codex_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\ClaudeLauncher-Publisher\signing-key.dpapi' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($redirected) { $KeyFile = $redirected; Write-Output ('Using signing key from Codex package cache: ' + $KeyFile) }
}
if (-not (Test-Path -LiteralPath $KeyFile)) { throw ('Signing key not found: ' + $KeyFile + '. Restore the DPAPI key on the publishing account first; do not generate a new one.') }

Step 'Build Windows Setup (tests and self-test included)'
& (Join-Path $PSScriptRoot 'setup.ps1') | Out-Null
$exe = Join-Path $projectRoot 'artifacts/bin/ClaudeLauncher.exe'
[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($exe)) | Out-Null
$version = [ClaudeLauncher.Updates]::DisplayVersion
$assemblyVersion = [ClaudeLauncher.Updates]::Version
$tag = if ($joint) { 'v' + $version } else { 'windows-v' + $version }
$setupName = [ClaudeLauncher.Updates]::FileName('windows', $version)
$setup = Join-Path $projectRoot ('artifacts/setup/' + $setupName)
if (-not (Test-Path -LiteralPath $setup)) { throw ('Setup not found: ' + $setup) }
$commit = (& git -C $projectRoot rev-parse HEAD).Trim()
if (-not $DryRun -and -not (& git -C $projectRoot branch -r --contains $commit)) { throw ('Commit ' + $commit + ' is not pushed. Push it before publishing.') }
$staging = Join-Path $projectRoot ('artifacts/release/' + $version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($staging) | Out-Null
Copy-Item -LiteralPath $setup -Destination (Join-Path $staging $setupName)
$legacyName = 'ClaudeLauncher-' + $assemblyVersion + '.exe'
Copy-Item -LiteralPath $exe -Destination (Join-Path $staging $legacyName)
$manifests = @{}

if ($joint) {
    $dmgName = [ClaudeLauncher.Updates]::FileName('macos', $version)
    Step ('Obtain macOS DMG ' + $dmgName + ' from draft ' + $tag)
    if (-not $DryRun) {
        $draft = (& $ghExe release view $tag --repo $Repository --json isDraft | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0 -or -not $draft) { throw ('Release ' + $tag + ' not found. macOS must upload the DMG to a draft release first.') }
        if (-not $draft.isDraft) { throw ('Release ' + $tag + ' is already published. Increase the version instead of republishing.') }
    }
    if ($MacDmg) { Copy-Item -LiteralPath $MacDmg -Destination (Join-Path $staging $dmgName) }
    else { Invoke-Gh release download $tag --repo $Repository --pattern $dmgName --dir $staging }
    if ((Get-Item -LiteralPath (Join-Path $staging $dmgName)).Length -ne $MacSize -or [ClaudeLauncher.Updates]::Hash((Join-Path $staging $dmgName)) -ne $MacSha256) { throw 'macOS DMG size or SHA-256 does not match the values given by macOS.' }
} else {
    Step 'Windows-only release: carry the current macOS manifest and DMG from the latest release'
    # PowerShell 5.1 在 Stop 模式下会把原生命令的 stderr 当作异常；这里“不存在”是预期结果，临时放宽。
    $ErrorActionPreference = 'Continue'
    & $ghExe release view $tag --repo $Repository --json tagName *> $null
    $exists = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($exists) { throw ('Release ' + $tag + ' already exists. Increase the version instead of republishing.') }
    $latestTag = (& $ghExe release view --repo $Repository --json tagName | ConvertFrom-Json).tagName
    if ($LASTEXITCODE -ne 0 -or -not $latestTag) { throw 'Cannot find the latest published release to carry macOS files from.' }
    Invoke-Gh release download $latestTag --repo $Repository --pattern 'latest-macos.json' --dir $staging
    $macRelease = [ClaudeLauncher.Updates]::Verify([IO.File]::ReadAllText((Join-Path $staging 'latest-macos.json')), [ClaudeLauncher.Updates]::PublicKey(), 'macos')
    $dmgName = $macRelease.file
    Invoke-Gh release download $latestTag --repo $Repository --pattern $dmgName --dir $staging
    if ((Get-Item -LiteralPath (Join-Path $staging $dmgName)).Length -ne $macRelease.size -or [ClaudeLauncher.Updates]::Hash((Join-Path $staging $dmgName)) -ne $macRelease.sha256) { throw ('macOS DMG in ' + $latestTag + ' does not match its manifest.') }
    $manifests['latest-macos.json'] = $macRelease
    Write-Output ('  carried macOS ' + $macRelease.version + ' from ' + $latestTag)
}

Step 'Sign manifests (key stays in memory)'
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$private = $null
try {
    $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($private))
    $verifyKey = [ClaudeLauncher.Updates]::PublicKey()
    if ($rsa.ToXmlString($false) -ne $verifyKey) {
        if (-not $DryRun) { throw 'Signing key does not match windows/assets/update-public-key.xml. Refusing to publish.' }
        Write-Output 'WARNING: test key (not the release key); output is for dry run only.'
        $verifyKey = $rsa.ToXmlString($false)
    }
    $sha256 = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    $serializer = New-Object Web.Script.Serialization.JavaScriptSerializer
    $entries = @(, @('windows', $setupName, '10.0.19041'))
    if ($joint) { $entries += , @('macos', $dmgName, '12.0') }
    foreach ($entry in $entries) {
        $file = Join-Path $staging $entry[1]
        $release = New-Object ClaudeLauncher.Release
        $release.platform = $entry[0]; $release.version = $version; $release.file = $entry[1]
        $release.size = (Get-Item -LiteralPath $file).Length; $release.sha256 = [ClaudeLauncher.Updates]::Hash($file); $release.minimumOS = $entry[2]
        $release.signature = [Convert]::ToBase64String($rsa.SignData([ClaudeLauncher.Updates]::Payload($release), $sha256))
        $json = $serializer.Serialize($release)
        [ClaudeLauncher.Updates]::Verify($json, $verifyKey, $entry[0]) | Out-Null
        $name = 'latest-' + $entry[0] + '.json'
        Write-Utf8 (Join-Path $staging $name) $json
        $manifests[$name] = $release
    }
    # 1.2.0 客户端：旧格式清单指向本版的单个 exe，由其 --apply-update 原地替换。
    $legacy = New-Object ClaudeLauncher.LegacyRelease
    $legacy.version = $assemblyVersion; $legacy.file = $legacyName
    $legacy.size = (Get-Item -LiteralPath (Join-Path $staging $legacyName)).Length; $legacy.sha256 = [ClaudeLauncher.Updates]::Hash((Join-Path $staging $legacyName))
    $legacy.signature = [Convert]::ToBase64String($rsa.SignData([ClaudeLauncher.Updates]::LegacyPayload($legacy), $sha256))
    $legacyJson = $serializer.Serialize($legacy)
    [ClaudeLauncher.Updates]::VerifyLegacy($legacyJson, $verifyKey) | Out-Null
    Write-Utf8 (Join-Path $staging 'latest.json') $legacyJson
} finally {
    $rsa.Dispose()
    if ($private) { [Array]::Clear($private, 0, $private.Length) }
}
if ($DryRun) {
    Step ('Dry run complete (' + $tag + '). Signed and verified locally; nothing uploaded. Staging: ' + $staging)
    Get-ChildItem -LiteralPath $staging | ForEach-Object { Write-Output ('  ' + $_.Name + '  ' + $_.Length + ' bytes') }
    return
}

Step 'Copy installers to self-hosted directory (not referenced until manifests switch)'
[IO.Directory]::CreateDirectory($PublicDirectory) | Out-Null
foreach ($name in @($setupName, $dmgName, $legacyName)) {
    $destination = Join-Path $PublicDirectory $name
    $source = Join-Path $staging $name
    if (Test-Path -LiteralPath $destination) {
        if ([ClaudeLauncher.Updates]::Hash($destination) -ne [ClaudeLauncher.Updates]::Hash($source)) { throw ($name + ' already exists on the self-hosted service with different bytes. Increase the version.') }
    } else { Copy-Item -LiteralPath $source -Destination $destination }
}

$assets = @((Join-Path $staging $setupName), (Join-Path $staging 'latest-windows.json'), (Join-Path $staging 'latest-macos.json'))
if ($joint) {
    Step ('Upload Setup and manifests to draft ' + $tag)
    Invoke-Gh release upload $tag @assets --repo $Repository --clobber
} else {
    Step ('Create draft ' + $tag + ' at ' + $commit + ' with Windows files and the carried macOS files')
    Invoke-Gh release create $tag @assets (Join-Path $staging $dmgName) --repo $Repository --draft --target $commit --title ('Windows ' + $version) --notes ('Windows ' + $version + '. macOS files carried from the previous release so GitHub fallback keeps working on both platforms.')
}

$switched = @()
$published = $false
try {
    Step 'Switch self-hosted manifests'
    $names = @('latest-windows.json', 'latest.json')
    if ($joint) { $names += 'latest-macos.json' }
    foreach ($name in $names) {
        $target = Join-Path $PublicDirectory $name
        $pending = $target + '.pending'
        Copy-Item -LiteralPath (Join-Path $staging $name) -Destination $pending -Force
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($pending, $target, ($target + '.previous')) } else { [IO.File]::Move($pending, $target) }
        $switched += $name
    }

    Step ('Publish ' + $tag)
    Invoke-Gh release edit $tag --repo $Repository --draft=false --latest
    $published = $true

    Step 'Verify both sources'
    $base = [ClaudeLauncher.Updates]::BaseUrl
    $sources = @(@('self-hosted', $base, 0), @('GitHub', [ClaudeLauncher.Updates]::GitHubBase, 5))
    foreach ($source in $sources) {
        foreach ($platform in @('windows', 'macos')) {
            $name = 'latest-' + $platform + '.json'
            $download = Join-Path $staging ('check-' + $source[0] + '-' + $name)
            Invoke-WebRequest -UseBasicParsing -Uri ($source[1] + $name) -OutFile $download -MaximumRedirection $source[2] -TimeoutSec 60
            $remote = [ClaudeLauncher.Updates]::Verify([IO.File]::ReadAllText($download), [ClaudeLauncher.Updates]::PublicKey(), $platform)
            $expected = $manifests[$name]
            # 只发布 Windows 时，自建服务的 macOS 清单保持原样，只要求验签通过且不低于随附版本。
            $sameAsExpected = $remote.version -eq $expected.version -and $remote.sha256 -eq $expected.sha256 -and $remote.size -eq $expected.size
            if (-not $sameAsExpected -and -not (-not $joint -and $platform -eq 'macos' -and $source[0] -eq 'self-hosted' -and [ClaudeLauncher.Updates]::Compare($remote.version, $expected.version) -ge 0)) { throw ($source[0] + ' serves a different ' + $name) }
            $package = Join-Path $staging ('check-' + $source[0] + '-' + $platform + '.bin')
            Invoke-WebRequest -UseBasicParsing -Uri ($source[1] + [Uri]::EscapeDataString($remote.file)) -OutFile $package -MaximumRedirection $source[2] -TimeoutSec 600
            if ((Get-Item -LiteralPath $package).Length -ne $remote.size -or [ClaudeLauncher.Updates]::Hash($package) -ne $remote.sha256) { throw ($source[0] + ' package does not match ' + $name) }
            Write-Output ('  ' + $source[0] + ' ' + $platform + ' ' + $remote.version + ': OK')
        }
    }
    Step ('Published ' + $tag + '. Staging kept at ' + $staging)
} catch {
    $failure = $_
    Write-Output ('FAILED: ' + $failure + ' -- rolling back.')
    foreach ($name in $switched) {
        $target = Join-Path $PublicDirectory $name
        if (Test-Path -LiteralPath ($target + '.previous')) { Copy-Item -LiteralPath ($target + '.previous') -Destination $target -Force }
        else { Remove-Item -LiteralPath $target }
    }
    if ($published) { & $ghExe release edit $tag --repo $Repository --draft=true }
    throw $failure
}
