param([string]$OutputPath = (Join-Path $PSScriptRoot 'build\AISwitchDesk.exe'))
$ErrorActionPreference = 'Stop'
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sourceDir = Join-Path $PSScriptRoot 'src'
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Management.dll','System.Numerics.dll') | ForEach-Object { '/r:' + (Join-Path $frameworkDir $_) }
$references += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll','WindowsFormsIntegration.dll','UIAutomationClient.dll','UIAutomationTypes.dll') | ForEach-Object { '/r:' + (Join-Path (Join-Path $frameworkDir 'WPF') $_) }
$references += '/r:' + (Join-Path $frameworkDir 'System.Xaml.dll')
$references += @('System.Runtime.dll','System.Runtime.InteropServices.WindowsRuntime.dll','System.ObjectModel.dll','System.Numerics.Vectors.dll') | ForEach-Object { '/r:' + (Join-Path $frameworkDir $_) }
$references += '/r:' + (Join-Path $frameworkDir 'System.Runtime.WindowsRuntime.dll')
$references += @('Windows.Graphics','Windows.UI','Windows.System','Windows.Foundation') | ForEach-Object { '/r:' + (Join-Path $env:WINDIR ('System32\WinMetadata\' + $_ + '.winmd')) }
$resourceArgs = @('chatgpt.png','claude-mark.png','compare-mark.png','desk-mark.png','Settings.xaml','ExitMenu.xaml') | ForEach-Object { '/resource:' + (Join-Path $sourceDir $_) + ',' + $_ }
$sourceArgs = Get-ChildItem -LiteralPath $sourceDir -Filter '*.cs' -Recurse | ForEach-Object FullName
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
& (Join-Path $frameworkDir 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /unsafe+ (('/out:' + $OutputPath)) (('/win32icon:' + (Join-Path $sourceDir 'app.ico'))) (('/win32manifest:' + (Join-Path $sourceDir 'app.manifest'))) @references @resourceArgs @sourceArgs
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $OutputPath
