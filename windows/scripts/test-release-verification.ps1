$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-verification.ps1')
$count = 0
foreach ($artifact in @('manifest', 'Setup', 'DMG')) {
    $result = Get-ReleaseVerificationResult -DownloadStep { throw 'Synthetic timeout' } -VerifyStep { throw 'Must not verify failed download' }
    if ($result.Status -ne 'Pending') { throw "$artifact timeout must preserve publication" }; $count++
    $result = Get-ReleaseVerificationResult -DownloadStep { } -VerifyStep { $true }
    if ($result.Status -ne 'Verified') { throw "$artifact correct content rejected" }; $count++
    $result = Get-ReleaseVerificationResult -DownloadStep { } -VerifyStep { $false }
    if ($result.Status -ne 'Mismatch') { throw "$artifact wrong content must roll back" }; $count++
}
# 调用方变量与复核函数的参数不能相互遮蔽（publish-release.ps1 用 $download 保存下载路径）。
$download = Join-Path ([IO.Path]::GetTempPath()) ('release-verification-' + [Guid]::NewGuid().ToString('N'))
$verify = 'caller value'
try {
    $result = Get-ReleaseVerificationResult -DownloadStep { [IO.File]::WriteAllText($download, 'fixture') } -VerifyStep { [IO.File]::ReadAllText($download) -eq 'fixture' -and $verify -eq 'caller value' }
    if ($result.Status -ne 'Verified') { throw "caller variables were shadowed: $($result.Status) $($result.Reason)" }; $count++
} finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
Write-Output "PASS: $count offline release-verification scenarios; no publication or network."
