param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$referenceDir = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $referenceDir)) { throw 'Install the .NET Framework 4.8 Developer Pack to build Orbit.' }
$outputDir = Join-Path $projectDir 'dist\Orbit'
$artifactDir = Join-Path $projectDir 'artifacts'
New-Item -ItemType Directory -Path $outputDir,$artifactDir -Force | Out-Null
$referenceNames = @('mscorlib','System','System.Core','System.Xml','System.Runtime.Serialization','System.Xaml','WindowsBase','PresentationCore','PresentationFramework','System.Drawing','System.Windows.Forms','UIAutomationTypes')
$references = $referenceNames | ForEach-Object { '/reference:' + (Join-Path $referenceDir ($_ + '.dll')) }
$common = @('/nologo','/noconfig','/nostdlib+','/codepage:65001','/utf8output','/optimize+','/platform:anycpu') + $references
$corePath = Join-Path $projectDir 'src\TimerEngine.cs'
$testExe = Join-Path $artifactDir 'TimerEngineTests.exe'
& $compiler @common /target:exe ('/out:' + $testExe) $corePath (Join-Path $projectDir 'tests\TimerEngineTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Core test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
$layoutTestExe = Join-Path $artifactDir 'PopupLayoutTests.exe'
& $compiler @common /target:exe ('/out:' + $layoutTestExe) (Join-Path $projectDir 'src\PopupLayout.cs') (Join-Path $projectDir 'tests\PopupLayoutTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Popup layout test compilation failed.' }
& $layoutTestExe
if ($LASTEXITCODE -ne 0) { throw 'Popup layout tests failed.' }
$clipboardTestExe = Join-Path $artifactDir 'ClipboardHistoryTests.exe'
& $compiler @common /target:exe ('/out:' + $clipboardTestExe) (Join-Path $projectDir 'src\ClipboardHistory.cs') (Join-Path $projectDir 'tests\ClipboardHistoryTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clipboard history test compilation failed.' }
& $clipboardTestExe
if ($LASTEXITCODE -ne 0) { throw 'Clipboard history tests failed.' }
$clipboardMonitorTestExe = Join-Path $artifactDir 'ClipboardMonitorTests.exe'
& $compiler @common /target:exe /main:Orbit.ClipboardMonitorTests ('/out:' + $clipboardMonitorTestExe) (Join-Path $projectDir 'src\ClipboardHistory.cs') (Join-Path $projectDir 'src\ClipboardMonitor.cs') (Join-Path $projectDir 'tests\ClipboardMonitorTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clipboard monitor test compilation failed.' }
& $clipboardMonitorTestExe
if ($LASTEXITCODE -ne 0) { throw 'Clipboard monitor tests failed.' }
$clipboardDragTestExe = Join-Path $artifactDir 'ClipboardDragExportTests.exe'
& $compiler @common /target:exe /main:Orbit.ClipboardDragExportTests ('/out:' + $clipboardDragTestExe) (Join-Path $projectDir 'src\ClipboardHistory.cs') (Join-Path $projectDir 'src\ClipboardMonitor.cs') (Join-Path $projectDir 'src\ClipboardDragExport.cs') (Join-Path $projectDir 'tests\ClipboardDragExportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clipboard drag export test compilation failed.' }
& $clipboardDragTestExe
if ($LASTEXITCODE -ne 0) { throw 'Clipboard drag export tests failed.' }
$clipboardDragInteropExe = Join-Path $artifactDir 'ClipboardDragInteropTests.exe'
& $compiler @common /target:exe /main:Orbit.ClipboardDragInteropTests ('/out:' + $clipboardDragInteropExe) (Join-Path $projectDir 'src\ClipboardHistory.cs') (Join-Path $projectDir 'src\ClipboardDragExport.cs') (Join-Path $projectDir 'tests\ClipboardDragInteropTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clipboard drag interop test compilation failed.' }
& $clipboardDragInteropExe
if ($LASTEXITCODE -ne 0) { throw 'Clipboard drag interop tests failed.' }
$exe = Join-Path $outputDir 'Orbit.exe'
$sources = @($corePath,(Join-Path $projectDir 'src\PlatformServices.cs'),(Join-Path $projectDir 'src\DesktopApp.cs'),(Join-Path $projectDir 'src\PopupLayout.cs'),(Join-Path $projectDir 'src\LauncherWindow.cs'),(Join-Path $projectDir 'src\TaskPanel.cs'),(Join-Path $projectDir 'src\UtilityPanels.cs'),(Join-Path $projectDir 'src\ClipboardHistory.cs'),(Join-Path $projectDir 'src\ClipboardMonitor.cs'),(Join-Path $projectDir 'src\ClipboardDragExport.cs'),(Join-Path $projectDir 'tests\UiSmokeTests.cs'))
& $compiler @common /target:winexe /main:Orbit.Program ('/out:' + $exe) ('/win32manifest:' + (Join-Path $projectDir 'src\app.manifest')) ('/resource:' + (Join-Path $projectDir 'src\Panel.xaml') + ',Orbit.Panel.xaml') @sources
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
Copy-Item -LiteralPath (Join-Path $projectDir 'src\Orbit.exe.config') -Destination $outputDir -Force
if (Test-Path -LiteralPath (Join-Path $projectDir 'README.md')) { Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination $outputDir -Force }
Write-Output ('Built: ' + $exe)
if ($SmokeTest) {
    $smokeDir = Join-Path $artifactDir ('ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $process = Start-Process -FilePath $exe -ArgumentList @('--smoke-test',('"' + $smokeDir + '"')) -WindowStyle Hidden -PassThru -Wait
    $resultPath = Join-Path $smokeDir 'ui-smoke-results.txt'
    if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath }
    if ($process.ExitCode -ne 0) { throw ('Desktop smoke test failed. Results: ' + $smokeDir) }
    if (!(Test-Path -LiteralPath $resultPath)) { throw ('Desktop smoke test did not produce a report. Check: ' + $smokeDir) }
}
