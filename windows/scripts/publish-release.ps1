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
# 上传后先经 GitHub API 下载草稿附件逐字节核对（不经 latest/download 缓存），不符则停在草稿，原 Latest 与自建服务不变。
# 转正式前记下上一版 Latest；只有内容确认不符才回滚：恢复自建清单、改回草稿并显式把上一版设回 Latest。
# latest/download 最多等 10 分钟；仅缓存未更新或自建服务暂时不可达时保留发布、列出未完成的复核并以状态 2 退出。
# -DryRun 只构建、签名、本地校验，不上传（允许测试密钥；同名 Release 已存在时只警告）。私钥只在内存中使用，不落盘、不输出。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-verification.ps1')
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
# Windows 清单只认共用密钥；Mac 清单可由共用密钥或 Mac 发布机自己的密钥签名（SPEC 签名与校验），任一验签通过即可。
$macKeyFile = Join-Path (Split-Path $projectRoot -Parent) 'resources/update-public-key-macos.xml'
function VerifyManifest([string]$json, [string]$platform) {
    $keys = @([ClaudeLauncher.Updates]::PublicKey())
    if ($platform -eq 'macos' -and (Test-Path -LiteralPath $macKeyFile)) { $keys += [IO.File]::ReadAllText($macKeyFile) }
    foreach ($key in $keys) { try { return [ClaudeLauncher.Updates]::Verify($json, $key, $platform) } catch { $failure = $_ } }
    throw $failure
}

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
    if ($exists -and -not $DryRun) { throw ('Release ' + $tag + ' already exists. Increase the version instead of republishing.') }
    if ($exists) { Write-Output ('WARNING: release ' + $tag + ' already exists; a real publish would stop here. Continuing the dry run.') }
    $latestTag = (& $ghExe release view --repo $Repository --json tagName | ConvertFrom-Json).tagName
    if ($LASTEXITCODE -ne 0 -or -not $latestTag) { throw 'Cannot find the latest published release to carry macOS files from.' }
    Invoke-Gh release download $latestTag --repo $Repository --pattern 'latest-macos.json' --dir $staging
    # Mac 清单可由共用密钥或 Mac 发布机自己的密钥签名（SPEC 签名与校验），任一验签通过即可原样带入。
    $macRelease = VerifyManifest ([IO.File]::ReadAllText((Join-Path $staging 'latest-macos.json'))) 'macos'
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

# 发布前经 GitHub API 下载草稿附件逐字节核对（不经 latest/download 缓存）；不符则停在草稿，原 Latest 与自建服务都不变。
Step 'Verify draft assets via the GitHub API (not the latest/download cache)'
$expectedAssets = @($setupName, 'latest-windows.json', 'latest-macos.json', $dmgName) | Sort-Object
$draftAssets = @((& $ghExe release view $tag --repo $Repository --json assets | ConvertFrom-Json).assets | ForEach-Object { $_.name }) | Sort-Object
if ($LASTEXITCODE -ne 0 -or (($draftAssets -join ',') -ne ($expectedAssets -join ','))) { throw ('Draft ' + $tag + ' assets are ' + ($draftAssets -join ',') + '; expected ' + ($expectedAssets -join ',') + '. Draft kept, not published.') }
$draftDir = Join-Path $staging 'draft'
[IO.Directory]::CreateDirectory($draftDir) | Out-Null
Invoke-Gh release download $tag --repo $Repository --dir $draftDir
foreach ($name in $expectedAssets) {
    $downloaded = Join-Path $draftDir $name
    if (-not (Test-Path -LiteralPath $downloaded) -or [ClaudeLauncher.Updates]::Hash($downloaded) -ne [ClaudeLauncher.Updates]::Hash((Join-Path $staging $name))) { throw ('Draft asset ' + $name + ' differs from the local file. Draft kept, not published.') }
}
Write-Output ('  draft assets match: ' + ($expectedAssets -join ', '))

# 转正式前记下上一版 Latest，回滚时显式恢复，避免 GitHub 自动选到更早的版本使 latest/download 失效。
$ErrorActionPreference = 'Continue'
$previousLatest = (& $ghExe api ('repos/' + $Repository + '/releases/latest') --jq .tag_name 2>$null)
$ErrorActionPreference = 'Stop'
if ($previousLatest) { $previousLatest = $previousLatest.Trim() }
Write-Output ('  previous Latest: ' + $(if ($previousLatest) { $previousLatest } else { '(none)' }))

$switched = @()
$published = $false
# 只在确认内容不符时调用：恢复自建清单，Release 改回草稿，并把上一版设回 Latest。
function Revert([string]$reason) {
    Write-Output ('FAILED: ' + $reason + ' -- rolling back.')
    foreach ($name in $script:switched) {
        $target = Join-Path $PublicDirectory $name
        if (Test-Path -LiteralPath ($target + '.previous')) { Copy-Item -LiteralPath ($target + '.previous') -Destination $target -Force }
        else { Remove-Item -LiteralPath $target }
    }
    if ($script:published) {
        $ErrorActionPreference = 'Continue'
        & $ghExe release edit $tag --repo $Repository --draft=true *> $null
        if ($previousLatest) { & $ghExe release edit $previousLatest --repo $Repository --latest *> $null }
        $ErrorActionPreference = 'Stop'
    }
    throw ($reason + ' (rolled back: ' + $tag + ' is a draft again' + $(if ($previousLatest) { ', ' + $previousLatest + ' restored as Latest' } else { '' }) + ')')
}
function Fetch([string]$uri, [string]$path, [int]$redirects, [int]$seconds) {
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $path -MaximumRedirection $redirects -TimeoutSec $seconds -Headers @{ 'Cache-Control' = 'no-cache' }
}

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
} catch { Revert ('publish step failed: ' + $_) }

$pendingChecks = @()
# 客户端已不再读取自建服务；过渡期仍为 1.7.3 及更早客户端发布并复核，地址只保留在发布脚本中。
Step 'Verify the self-hosted service (transition for 1.7.3 and older clients)'
$base = 'https://www.u4139693.nyat.app:53569/claude-launcher/'
foreach ($platform in @('windows', 'macos')) {
    $name = 'latest-' + $platform + '.json'
    $download = Join-Path $staging ('check-self-hosted-' + $name)
    try { Fetch ($base + $name) $download 0 60 } catch { $pendingChecks += ('self-hosted ' + $name + ' unreachable: ' + $_.Exception.Message); continue }
    try { $remote = VerifyManifest ([IO.File]::ReadAllText($download)) $platform } catch { Revert ('self-hosted ' + $name + ' fails verification') }
    $expected = $manifests[$name]
    # 只发布 Windows 时，自建服务的 macOS 清单保持原样，只要求验签通过且不低于随附版本。
    $sameAsExpected = $remote.version -eq $expected.version -and $remote.sha256 -eq $expected.sha256 -and $remote.size -eq $expected.size
    if (-not $sameAsExpected -and -not (-not $joint -and $platform -eq 'macos' -and [ClaudeLauncher.Updates]::Compare($remote.version, $expected.version) -ge 0)) { Revert ('self-hosted serves a different ' + $name) }
    $package = Join-Path $staging ('check-self-hosted-' + $platform + '.bin')
    try { Fetch ($base + [Uri]::EscapeDataString($remote.file)) $package 0 600 } catch { $pendingChecks += ('self-hosted ' + $remote.file + ' unreachable: ' + $_.Exception.Message); continue }
    if ((Get-Item -LiteralPath $package).Length -ne $remote.size -or [ClaudeLauncher.Updates]::Hash($package) -ne $remote.sha256) { Revert ('self-hosted package does not match ' + $name) }
    Write-Output ('  self-hosted ' + $platform + ' ' + $remote.version + ': OK')
}

# latest/download 由 GitHub 缓存，最多等 10 分钟；只有内容确认不符才回滚，缓存未更新则保留发布并以状态 2 提示稍后复核。
Step 'Wait for GitHub latest/download to serve the new release (up to 10 minutes)'
$github = [ClaudeLauncher.Updates]::GitHubBase
$served = $null
for ($attempt = 1; $attempt -le 40; $attempt++) {
    try {
        Fetch ($github + 'latest-windows.json') (Join-Path $staging 'check-github-latest-windows.json') 5 60
        $served = (VerifyManifest ([IO.File]::ReadAllText((Join-Path $staging 'check-github-latest-windows.json'))) 'windows').version
        if ($served -eq $version) { break }
    } catch { $served = $null }
    Start-Sleep -Seconds 15
}
if ($served -ne $version) {
    $pendingChecks += ('GitHub latest/download still serves ' + $(if ($served) { $served } else { 'nothing readable' }) + ' after 10 minutes (cache not updated; assets were already verified via the API)')
} else {
    foreach ($name in @('latest-windows.json', 'latest-macos.json', $setupName, $dmgName)) {
        $download = Join-Path $staging ('check-github-' + $name)
        $verification = Get-ReleaseVerificationResult -Download { Fetch ($github + [Uri]::EscapeDataString($name)) $download 5 600 } -Verify { [ClaudeLauncher.Updates]::Hash($download) -eq [ClaudeLauncher.Updates]::Hash((Join-Path $staging $name)) }
        if ($verification.Status -eq 'Pending') { $pendingChecks += ('GitHub ' + $name + ' pending verification: ' + $verification.Reason); continue }
        if ($verification.Status -eq 'Mismatch') { Revert ('GitHub latest/download serves a different ' + $name) }
        Write-Output ('  GitHub ' + $name + ': OK')
    }
}

if ($pendingChecks.Count -gt 0) {
    Write-Output ('Published ' + $tag + ' but some checks could not complete (not rolled back):')
    $pendingChecks | ForEach-Object { Write-Output ('  - ' + $_) }
    Write-Output ('Re-check later. Staging kept at ' + $staging)
    exit 2
}
Step ('Published ' + $tag + '. Staging kept at ' + $staging)
