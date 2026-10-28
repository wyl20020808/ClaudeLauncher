$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('claude-install-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$rsa.PersistKeyInCsp = $false
$key = Join-Path $fixture 'test-public.xml'
[IO.File]::WriteAllText($key, $rsa.ToXmlString($false))
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$arguments = @('/nologo','/target:exe','/main:InstallHarness','/reference:System.dll','/reference:System.Core.dll','/reference:System.Management.dll','/reference:System.Xaml.dll','/reference:System.Web.Extensions.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll')
foreach ($name in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $arguments += '/reference:' + (Join-Path $framework ('WPF/' + $name)) }
$arguments += '/resource:' + $key + ',ClaudeLauncher.UpdateKey'
$sources = @('src/ClaudeLauncher.cs','src/ProfileDeletion.cs','src/Updates.cs','src/Diagnostics.cs','src/Storage.cs','src/LauncherUI.cs','tests/InstallHarness.cs') | ForEach-Object { Join-Path $projectRoot $_ }
foreach ($kind in @('old','new')) {
    $folder = Join-Path $fixture $kind
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $extra = @(); if ($kind -eq 'old') { $extra += '/define:OLD' }
    & $compiler @arguments @extra ('/out:' + (Join-Path $folder 'ClaudeLauncher.exe')) @sources
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compile failed' }
}
[Reflection.Assembly]::LoadFile((Join-Path $projectRoot 'artifacts/bin/ClaudeLauncher.exe')) | Out-Null
$checks = 0
foreach ($scenario in @('success','rollback','tamper')) {
    $incoming = Join-Path $fixture ($scenario + '/incoming'); $installed = Join-Path $fixture ($scenario + '/installed')
    [IO.Directory]::CreateDirectory($incoming) | Out-Null; [IO.Directory]::CreateDirectory($installed) | Out-Null
    $source = Join-Path $incoming 'ClaudeLauncher.exe'; $target = Join-Path $installed 'ClaudeLauncher.exe'
    [IO.File]::Copy((Join-Path $fixture 'new/ClaudeLauncher.exe'), $source)
    [IO.File]::Copy((Join-Path $fixture 'old/ClaudeLauncher.exe'), $target)
    $oldHash = [ClaudeLauncher.Updates]::Hash($target)
    $release = New-Object ClaudeLauncher.LegacyRelease
    $release.version='1.1.0.0'; $release.file='ClaudeLauncher-1.1.0.0.exe'; $release.size=(Get-Item -LiteralPath $source).Length; $release.sha256=[ClaudeLauncher.Updates]::Hash($source)
    $release.signature=[Convert]::ToBase64String($rsa.SignData([ClaudeLauncher.Updates]::LegacyPayload($release),[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')))
    if ($scenario -eq 'tamper') { $release.sha256 = '0' * 64 }
    [IO.File]::WriteAllText((Join-Path $incoming 'release.json'), ($release | ConvertTo-Json -Compress))
    [IO.File]::WriteAllText((Join-Path $incoming 'plan.json'), (@{target=$target;originalHash=$oldHash;pid=2147483647;started=0} | ConvertTo-Json -Compress))
    if ($scenario -eq 'rollback') { [IO.File]::WriteAllText((Join-Path $incoming 'simulate-crash'),'fixture') }
    $process = Start-Process -FilePath $source -ArgumentList '--apply-update' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw 'Installer fixture timed out; inspect fixture manually.' }
    $actual = [ClaudeLauncher.Updates]::Hash($target)
    if ($scenario -eq 'success') {
        if ($process.ExitCode -ne 0 -or $actual -ne $release.sha256 -or -not (Test-Path (Join-Path $incoming 'ready'))) { throw 'Success path failed' }
    } else {
        if ($process.ExitCode -eq 0 -or $actual -ne $oldHash) { throw ('Preserve/rollback failed: ' + $scenario) }
    }
    $checks++
    Write-Output ('PASS installer scenario: ' + $scenario)
}
$rsa.Dispose()
# 保留很小的测试夹作为验证证据，不递归删除任何目录。
Write-Output ('PASS: ' + $checks + ' installer integration scenarios. Evidence: ' + $fixture)
