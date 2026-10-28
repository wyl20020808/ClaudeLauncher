$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
[IO.Directory]::CreateDirectory((Join-Path $projectRoot 'artifacts/bin')) | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$arguments=@('/nologo','/target:exe','/main:UiPreview','/reference:System.dll','/reference:System.Core.dll','/reference:System.Management.dll','/reference:System.Xaml.dll','/reference:System.Web.Extensions.dll')
foreach($name in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $arguments += '/reference:'+(Join-Path $framework ('WPF/'+$name)) }
$arguments += '/resource:'+(Join-Path $projectRoot 'assets/claude.ico')+',ClaudeLauncher.Icon'
$arguments += '/resource:'+(Join-Path $projectRoot 'assets/update-public-key.xml')+',ClaudeLauncher.UpdateKey'
$sources=@('src/ClaudeLauncher.cs','src/ProfileDeletion.cs','src/Diagnostics.cs','src/Updates.cs','tests/UiPreview.cs') | ForEach-Object {Join-Path $projectRoot $_}
$output=Join-Path $projectRoot 'artifacts/bin/UiPreview.exe'
& $compiler @arguments ('/out:'+$output) @sources
if($LASTEXITCODE -ne 0) {throw 'Preview compile failed'}
& $output (Join-Path $projectRoot 'artifacts/previews')
if($LASTEXITCODE -ne 0) {throw 'Preview failed'}
