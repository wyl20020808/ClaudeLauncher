$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'server.py'
$listener = Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    $owner = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $listener[0].OwningProcess)
    if ($owner.CommandLine -and $owner.CommandLine.Contains($script)) { Write-Output 'Update server already running.'; exit 0 }
    throw 'Port 8080 is in use by another program. No process was stopped.'
}
$python = (& python -c 'import sys; print(sys.executable)').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Python was not found on the publishing computer.' }
$pythonw = Join-Path (Split-Path $python -Parent) 'pythonw.exe'
if (-not (Test-Path -LiteralPath $pythonw)) { throw 'pythonw.exe was not found.' }
$service = Start-Process -FilePath $pythonw -ArgumentList ('"' + $script + '"') -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 500
$service.Refresh()
if ($service.HasExited) { throw 'Server exited. Check server-error.txt.' }
Write-Output ('Update server PID=' + $service.Id + ' listening on 127.0.0.1:8080')
