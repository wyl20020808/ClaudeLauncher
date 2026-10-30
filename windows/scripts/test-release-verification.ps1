$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-verification.ps1')
$count = 0
foreach ($artifact in @('manifest', 'Setup', 'DMG')) {
    $result = Get-ReleaseVerificationResult -Download { throw 'Synthetic timeout' } -Verify { throw 'Must not verify failed download' }
    if ($result.Status -ne 'Pending') { throw "$artifact timeout must preserve publication" }; $count++
    $result = Get-ReleaseVerificationResult -Download { } -Verify { $true }
    if ($result.Status -ne 'Verified') { throw "$artifact correct content rejected" }; $count++
    $result = Get-ReleaseVerificationResult -Download { } -Verify { $false }
    if ($result.Status -ne 'Mismatch') { throw "$artifact wrong content must roll back" }; $count++
}
Write-Output "PASS: $count offline release-verification scenarios; no publication or network."
