$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$keyDirectory = Join-Path $env:LOCALAPPDATA 'ClaudeLauncher-Publisher'
$keyFile = Join-Path $keyDirectory 'signing-key.dpapi'
$publicFile = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets/update-public-key.xml'
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
try {
    if (Test-Path -LiteralPath $keyFile) {
        $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($private))
    } else {
        if (Test-Path -LiteralPath $publicFile) { throw 'Public key exists but private key is missing. Refusing to replace the release identity.' }
        [IO.Directory]::CreateDirectory($keyDirectory) | Out-Null
        $private = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        $protected = [Security.Cryptography.ProtectedData]::Protect($private, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($keyFile, $protected)
    }
    $public = $rsa.ToXmlString($false)
    if ((Test-Path -LiteralPath $publicFile) -and ([IO.File]::ReadAllText($publicFile) -ne $public)) { throw 'Release key mismatch.' }
    [IO.File]::WriteAllText($publicFile, $public)
    Write-Output 'Update public key ready; private key encrypted with Windows DPAPI CurrentUser.'
} finally { $rsa.Dispose(); if ($private) { [Array]::Clear($private, 0, $private.Length) } }
