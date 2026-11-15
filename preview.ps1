<#
Renders the rail in every state and theme to PNG sheets in build\preview,
using the sidebar's own drawing code. Nothing is installed or launched.

    .\preview.ps1            states.png and themes.png
    .\preview.ps1 -Frames    also every frame of the animations
    .\preview.ps1 -Windows   also pictures of the built sidebar's settings, popups and menu,
                             opened with sample data (needs build\AISwitchDesk.exe)
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'build\preview'),
    [string]$App = (Join-Path $PSScriptRoot 'build\AISwitchDesk.exe'),
    [double]$Scale = 2,
    [switch]$Frames,
    [switch]$Windows
)
$ErrorActionPreference = 'Stop'
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $PSScriptRoot 'build\RailPreview.exe'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exe) | Out-Null
$sources = @(Join-Path $PSScriptRoot 'tools\RailPreview.cs')
$sources += @('RailArt','ComparisonGlyph','DeskTheme','Ease','RailAction') |
    ForEach-Object { Join-Path $PSScriptRoot "src\AISwitchDesk\$_.cs" }
$references = @('System.dll','System.Core.dll','System.Drawing.dll') | ForEach-Object { '/r:' + (Join-Path $frameworkDir $_) }
& (Join-Path $frameworkDir 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /nowarn:0649 ('/out:' + $exe) @references @sources
if ($LASTEXITCODE -ne 0) { throw 'The preview did not compile.' }
$arguments = @((Join-Path $PSScriptRoot 'src'), $Output, $Scale.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($Frames) { $arguments += '--frames' }
& $exe @arguments
if ($LASTEXITCODE -ne 0) { throw 'The preview did not render.' }

if ($Windows) {
    if (!(Test-Path -LiteralPath $App)) { throw "Missing $App. Run build.ps1 first." }
    $shots = Join-Path $PSScriptRoot 'build\WindowShots.exe'
    $wpf = Join-Path $frameworkDir 'WPF'
    $references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') |
        ForEach-Object { '/r:' + (Join-Path $frameworkDir $_) }
    $references += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll') | ForEach-Object { '/r:' + (Join-Path $wpf $_) }
    & (Join-Path $frameworkDir 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ ('/out:' + $shots) @references (Join-Path $PSScriptRoot 'tools\WindowShots.cs')
    if ($LASTEXITCODE -ne 0) { throw 'The window pictures did not compile.' }
    & $shots (Resolve-Path -LiteralPath $App).Path (Join-Path $Output 'windows')
    if ($LASTEXITCODE -ne 0) { throw 'The window pictures did not render.' }
}
