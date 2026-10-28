param([string]$Executable = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/bin/ClaudeLauncher.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($Executable))
$version = $assembly.GetName().Version.ToString()
$release = New-Object ClaudeLauncher.Release
$release.version = $version
$release.file = 'ClaudeLauncher-' + $version + '.exe'
$release.size = (Get-Item -LiteralPath $Executable).Length
$release.sha256 = [ClaudeLauncher.Updates]::Hash($Executable)
$keyFile = Join-Path $env:LOCALAPPDATA 'ClaudeLauncher-Publisher/signing-key.dpapi'
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
try {
    $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($private))
    if ($rsa.ToXmlString($false) -ne [ClaudeLauncher.Updates]::PublicKey()) { throw 'Signing identity does not match the client.' }
    $release.signature = [Convert]::ToBase64String($rsa.SignData([ClaudeLauncher.Updates]::Payload($release), [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')))
    $json = $release | ConvertTo-Json -Compress
    [ClaudeLauncher.Updates]::Verify($json, $rsa.ToXmlString($false)) | Out-Null
    $public = Join-Path (Split-Path $PSScriptRoot -Parent) 'updates/public'
    [IO.Directory]::CreateDirectory($public) | Out-Null
    $destination = Join-Path $public $release.file
    if (Test-Path -LiteralPath $destination) {
        if ([ClaudeLauncher.Updates]::Hash($destination) -ne $release.sha256) { throw 'Version already published with different bytes. Increase the version first.' }
    } else { [IO.File]::Copy([IO.Path]::GetFullPath($Executable), $destination) }
    $temporary = Join-Path $public 'latest.json.pending'
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    $manifest = Join-Path $public 'latest.json'
    if (Test-Path -LiteralPath $manifest) { [IO.File]::Replace($temporary, $manifest, (Join-Path $public 'latest.previous.json')) } else { [IO.File]::Move($temporary, $manifest) }
    Write-Output ('Published version ' + $version + ', SHA256=' + $release.sha256)
} finally { $rsa.Dispose(); if ($private) { [Array]::Clear($private, 0, $private.Length) } }
