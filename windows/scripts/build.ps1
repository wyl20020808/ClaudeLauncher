param([switch]$Preview)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $projectRoot 'artifacts/bin'
[IO.Directory]::CreateDirectory($bin) | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.dll', 'System.Core.dll', 'System.Management.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
foreach ($name in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll')) { $references += Join-Path $framework ('WPF\' + $name) }
$arguments = @('/nologo', '/optimize+', '/platform:anycpu', '/utf8output')
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
$icon = Join-Path $projectRoot 'assets/app.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Missing assets/app.ico. Run scripts/make-icon.ps1.' }
$arguments += '/resource:' + $icon + ',ClaudeLauncher.Icon'
$arguments += '/resource:' + (Join-Path $projectRoot 'assets/update-public-key.xml') + ',ClaudeLauncher.UpdateKey'
# 界面插画：仓库中的 WebP 在构建时转为 JPEG 内嵌，客户电脑无需 WebP 解码器。
Add-Type -AssemblyName PresentationCore
$art = Join-Path (Split-Path $projectRoot -Parent) 'resources/ui'
$ui = Join-Path $projectRoot 'artifacts/ui'
[IO.Directory]::CreateDirectory($ui) | Out-Null
$widths = [ordered]@{ 'background' = 1254; 'sidebar' = 544; 'card-1' = 640; 'card-2' = 640; 'card-3' = 640; 'card-4' = 640 }
foreach ($name in $widths.Keys) {
    $source = Join-Path $art ($name + '.webp'); $target = Join-Path $ui ($name + '.jpg')
    if (-not (Test-Path -LiteralPath $source)) { throw ('Missing UI artwork: ' + $source) }
    if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).LastWriteTimeUtc -lt (Get-Item -LiteralPath $source).LastWriteTimeUtc) {
        $stream = [IO.File]::OpenRead($source)
        try { $frame = [Windows.Media.Imaging.BitmapDecoder]::Create($stream, 'None', 'OnLoad').Frames[0] } finally { $stream.Close() }
        $scale = [Math]::Min(1.0, $widths[$name] / $frame.PixelWidth)
        $encoder = New-Object Windows.Media.Imaging.JpegBitmapEncoder
        $encoder.QualityLevel = 90
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create((New-Object Windows.Media.Imaging.TransformedBitmap($frame, (New-Object Windows.Media.ScaleTransform($scale, $scale))))))
        $file = [IO.File]::Create($target)
        try { $encoder.Save($file) } finally { $file.Close() }
    }
    $arguments += '/resource:' + $target + ',ClaudeLauncher.UI.' + $name
}
$output = Join-Path $bin 'ClaudeLauncher.exe'
$sources = @((Join-Path $projectRoot 'src/ClaudeLauncher.cs'), (Join-Path $projectRoot 'src/ProfileDeletion.cs'), (Join-Path $projectRoot 'src/Diagnostics.cs'), (Join-Path $projectRoot 'src/Updates.cs'), (Join-Path $projectRoot 'src/Usage.cs'), (Join-Path $projectRoot 'src/Settings.cs'), (Join-Path $projectRoot 'src/Handoff.cs'), (Join-Path $projectRoot 'src/ProxyCheck.cs'), (Join-Path $projectRoot 'src/Storage.cs'), (Join-Path $projectRoot 'src/LauncherUI.cs'))
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
if ($Preview) {
    # 离屏渲染四个页面到 artifacts/previews，不打开桌面窗口、不启动 Claude。
    $previewExe = Join-Path $bin 'UiPreview.exe'
    & $compiler @arguments /target:exe /main:UiPreview ('/out:' + $previewExe) @sources (Join-Path $projectRoot 'tests/UiPreview.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
    & $previewExe (Join-Path $projectRoot 'artifacts/previews')
    if ($LASTEXITCODE -ne 0) { throw 'Preview failed.' }
}
Write-Output $output
