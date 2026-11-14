<#
Renders the rail in every state and theme to PNG sheets in build\preview,
using the sidebar's own drawing code. Nothing is installed or launched.

    .\preview.ps1            states.png and themes.png
    .\preview.ps1 -Frames    also every frame of the animations
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'build\preview'),
    [double]$Scale = 2,
    [switch]$Frames
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
