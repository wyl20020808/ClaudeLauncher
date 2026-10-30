function Get-ReleaseVerificationResult {
    param([Parameter(Mandatory=$true)][scriptblock]$Download, [Parameter(Mandatory=$true)][scriptblock]$Verify)
    try { & $Download | Out-Null }
    catch { return [pscustomobject]@{ Status = 'Pending'; Reason = $_.Exception.Message } }
    try {
        if (& $Verify) { return [pscustomobject]@{ Status = 'Verified'; Reason = '' } }
        return [pscustomobject]@{ Status = 'Mismatch'; Reason = 'Downloaded content differs from the expected release.' }
    } catch { return [pscustomobject]@{ Status = 'Mismatch'; Reason = $_.Exception.Message } }
}
