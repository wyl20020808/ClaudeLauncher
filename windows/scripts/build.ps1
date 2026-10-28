$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $projectRoot 'artifacts/bin'
[IO.Directory]::CreateDirectory($bin) | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Management.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll')
foreach ($name in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) { $references += Join-Path $framework ('WPF\' + $name) }
$arguments = @('/nologo', '/optimize+', '/platform:anycpu', '/utf8output')
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
$icon = Join-Path $projectRoot 'assets/claude.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Missing claude.ico. Extract the icon from the locally installed Claude executable first.' }
$arguments += '/resource:' + $icon + ',ClaudeLauncher.Icon'
$arguments += '/resource:' + (Join-Path $projectRoot 'assets/update-public-key.xml') + ',ClaudeLauncher.UpdateKey'
$output = Join-Path $bin 'ClaudeLauncher.exe'
$sources = @((Join-Path $projectRoot 'src/ClaudeLauncher.cs'), (Join-Path $projectRoot 'src/ProfileDeletion.cs'), (Join-Path $projectRoot 'src/Diagnostics.cs'), (Join-Path $projectRoot 'src/Updates.cs'), (Join-Path $projectRoot 'src/Storage.cs'))
& $compiler @arguments /target:winexe ('/win32icon:' + $icon) ('/win32manifest:' + (Join-Path $projectRoot 'src/app.manifest')) ('/out:' + $output) @sources
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed.' }
$test = Join-Path $bin 'LauncherTests.exe'
& $compiler @arguments /target:exe /main:LauncherTests ('/out:' + $test) @sources (Join-Path $projectRoot 'tests/LauncherTests.cs') (Join-Path $projectRoot 'tests/DeletionTests.cs') (Join-Path $projectRoot 'tests/UpdateTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $test
if ($LASTEXITCODE -ne 0) { throw 'Launcher tests failed.' }
# GUI 子系统程序经管道才会等待退出并返回输出。
& $output --self-test | Write-Output
if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
Write-Output $output
