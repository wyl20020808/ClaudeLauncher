param(
    [Parameter(Mandatory = $true)][long]$MacSize,        # macOS 端 dmg.sh 给出的 size
    [Parameter(Mandatory = $true)][string]$MacSha256,    # macOS 端 dmg.sh 给出的 sha256
    [string]$MacDmg = '',                                # 本地 DMG；为空时从草稿 Release 下载
    [string]$KeyFile = (Join-Path $env:LOCALAPPDATA 'ClaudeLauncher-Publisher/signing-key.dpapi'),
    [string]$PublicDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'updates/public'),
    [string]$Repository = 'wyl20020808/ClaudeLauncher',
    [switch]$DryRun                                      # 只构建、签名、本地校验，不上传；允许测试密钥
)
# SPEC 更新与安装 · 发布流程：构建 Setup → 取得并核对 macOS DMG → 签名两份清单（及 1.2.0 兼容清单）→
# 上传 GitHub 草稿 → 切换自建服务清单 → 草稿转正式 → 两个来源各下载一次验签核对。
# 任一步失败都不留下半成品：正式发布之后的失败会恢复自建清单并把 Release 改回草稿。私钥只在内存中使用，不落盘、不输出。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Web.Extensions
$projectRoot = Split-Path $PSScriptRoot -Parent
function Step([string]$text) { Write-Output ('== ' + $text) }
function Gh {
    & gh @args
    if ($LASTEXITCODE -ne 0) { throw ('gh ' + ($args -join ' ') + ' failed.') }
}
function Write-Utf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false))) }

if (-not $DryRun) {
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in. Run: gh auth login' }
}
# 在 Codex 桌面版（打包应用）中运行 init-update-key.ps1 时，%LOCALAPPDATA% 写入被重定向到其包缓存；同一账户下 DPAPI 照常解密。
if (-not $PSBoundParameters.ContainsKey('KeyFile') -and -not (Test-Path -LiteralPath $KeyFile)) {
    $redirected = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'OpenAI.Codex_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\ClaudeLauncher-Publisher\signing-key.dpapi' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($redirected) { $KeyFile = $redirected; Write-Output ('Using signing key from Codex package cache: ' + $KeyFile) }
}
if (-not (Test-Path -LiteralPath $KeyFile)) { throw ('Signing key not found: ' + $KeyFile + '. Restore the DPAPI key on the publishing account first; do not generate a new one.') }
if ($MacSha256 -notmatch '^[a-f0-9]{64}$') { throw 'MacSha256 must be 64 lowercase hex characters.' }

Step 'Build Windows Setup (tests and self-test included)'
& (Join-Path $PSScriptRoot 'setup.ps1') | Out-Null
$exe = Join-Path $projectRoot 'artifacts/bin/ClaudeLauncher.exe'
[Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($exe)) | Out-Null
$version = [ClaudeLauncher.Updates]::DisplayVersion
$assemblyVersion = [ClaudeLauncher.Updates]::Version
$tag = 'v' + $version
$setupName = [ClaudeLauncher.Updates]::FileName('windows', $version)
$dmgName = [ClaudeLauncher.Updates]::FileName('macos', $version)
$setup = Join-Path $projectRoot ('artifacts/setup/' + $setupName)
if (-not (Test-Path -LiteralPath $setup)) { throw ('Setup not found: ' + $setup) }
$staging = Join-Path $projectRoot ('artifacts/release/' + $version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($staging) | Out-Null
Copy-Item -LiteralPath $setup -Destination (Join-Path $staging $setupName)
$legacyName = 'ClaudeLauncher-' + $assemblyVersion + '.exe'
Copy-Item -LiteralPath $exe -Destination (Join-Path $staging $legacyName)

Step ('Obtain macOS DMG ' + $dmgName)
if (-not $DryRun) {
    $draft = (& gh release view $tag --repo $Repository --json isDraft | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or -not $draft) { throw ('Release ' + $tag + ' not found. macOS must upload the DMG to a draft release first.') }
    if (-not $draft.isDraft) { throw ('Release ' + $tag + ' is already published. Increase the version instead of republishing.') }
}
if ($MacDmg) { Copy-Item -LiteralPath $MacDmg -Destination (Join-Path $staging $dmgName) }
else { Gh release download $tag --repo $Repository --pattern $dmgName --dir $staging }
$dmg = Join-Path $staging $dmgName
if ((Get-Item -LiteralPath $dmg).Length -ne $MacSize -or [ClaudeLauncher.Updates]::Hash($dmg) -ne $MacSha256) { throw 'macOS DMG size or SHA-256 does not match the values given by macOS.' }

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
    $manifests = @{}
    foreach ($entry in @(@('windows', $setupName, '10.0.19041'), @('macos', $dmgName, '12.0'))) {
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
    # 1.2.0 客户端：旧格式清单指向 1.7 的单个 exe，由其 --apply-update 原地替换。
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
    Step ('Dry run complete. Signed and verified locally; nothing uploaded. Staging: ' + $staging)
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

Step ('Upload Setup and manifests to draft ' + $tag)
Gh release upload $tag (Join-Path $staging $setupName) (Join-Path $staging 'latest-windows.json') (Join-Path $staging 'latest-macos.json') --repo $Repository --clobber

$switched = @()
$published = $false
try {
    Step 'Switch self-hosted manifests'
    foreach ($name in @('latest-windows.json', 'latest-macos.json', 'latest.json')) {
        $target = Join-Path $PublicDirectory $name
        $pending = $target + '.pending'
        Copy-Item -LiteralPath (Join-Path $staging $name) -Destination $pending -Force
        if (Test-Path -LiteralPath $target) { [IO.File]::Replace($pending, $target, ($target + '.previous')) } else { [IO.File]::Move($pending, $target) }
        $switched += $name
    }

    Step ('Publish ' + $tag)
    Gh release edit $tag --repo $Repository --draft=false --latest
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
            if ($remote.version -ne $expected.version -or $remote.sha256 -ne $expected.sha256 -or $remote.size -ne $expected.size) { throw ($source[0] + ' serves a different ' + $name) }
            $package = Join-Path $staging ('check-' + $source[0] + '-' + $platform + '.bin')
            Invoke-WebRequest -UseBasicParsing -Uri ($source[1] + [Uri]::EscapeDataString($remote.file)) -OutFile $package -MaximumRedirection $source[2] -TimeoutSec 600
            if ((Get-Item -LiteralPath $package).Length -ne $remote.size -or [ClaudeLauncher.Updates]::Hash($package) -ne $remote.sha256) { throw ($source[0] + ' package does not match ' + $name) }
            Write-Output ('  ' + $source[0] + ' ' + $platform + ': OK')
        }
    }
    Step ('Published ' + $version + '. Staging kept at ' + $staging)
} catch {
    $failure = $_
    Write-Output ('FAILED: ' + $failure + ' -- rolling back.')
    foreach ($name in $switched) {
        $target = Join-Path $PublicDirectory $name
        if (Test-Path -LiteralPath ($target + '.previous')) { Copy-Item -LiteralPath ($target + '.previous') -Destination $target -Force }
        else { Remove-Item -LiteralPath $target }
    }
    if ($published) { & gh release edit $tag --repo $Repository --draft=true }
    throw $failure
}
