function Get-ReleaseVerificationResult {
    # 脚本块在本函数作用域内执行，能看到这里的变量且不分大小写：参数名不得与调用方变量（如 $download）相同。
    param([Parameter(Mandatory=$true)][scriptblock]$DownloadStep, [Parameter(Mandatory=$true)][scriptblock]$VerifyStep)
    try { & $DownloadStep | Out-Null }
    catch { return [pscustomobject]@{ Status = 'Pending'; Reason = $_.Exception.Message } }
    try {
        if (& $VerifyStep) { return [pscustomobject]@{ Status = 'Verified'; Reason = '' } }
        return [pscustomobject]@{ Status = 'Mismatch'; Reason = 'Downloaded content differs from the expected release.' }
    } catch { return [pscustomobject]@{ Status = 'Mismatch'; Reason = $_.Exception.Message } }
}
