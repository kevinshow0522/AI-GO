<#
Builds the sidebar and its regression programs, then runs them.

    .\test.ps1                 build, compile every test, run them all
    .\test.ps1 -CompileOnly    stop after compiling
    .\test.ps1 -Only NativeDrop,Verify
    .\test.ps1 -SkipBuild      test the existing build\AISwitchDesk.exe

The tests open real windows and drag real files. Leave the mouse and keyboard
alone while they run.
#>
param(
    [string]$App = (Join-Path $PSScriptRoot 'build\AISwitchDesk.exe'),
    # Verify compares against an earlier build. Defaults to the installed sidebar.
    [string]$Baseline = (Join-Path $env:LOCALAPPDATA 'Programs\AI Switch Desk\AISwitchDesk.exe'),
    # A folder holding the Microsoft.Web.WebView2 package, for the new-chat tests.
    [string]$WebView2 = (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2'),
    [string[]]$Only,
    [switch]$SkipBuild,
    [switch]$CompileOnly,
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$outDir = Join-Path $PSScriptRoot 'build\tests'

if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $App | Out-Null }
if (!(Test-Path -LiteralPath $App)) { throw "Missing $App. Run build.ps1 first." }
$App = (Resolve-Path -LiteralPath $App).Path
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Xaml.dll') |
    ForEach-Object { '/r:' + (Join-Path $frameworkDir $_) }
$references += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll','WindowsFormsIntegration.dll','UIAutomationClient.dll','UIAutomationTypes.dll','UIAutomationProvider.dll') |
    ForEach-Object { '/r:' + (Join-Path (Join-Path $frameworkDir 'WPF') $_) }

function Find-WebView2([string]$name, [string]$hint) {
    if (!(Test-Path -LiteralPath $WebView2)) { return $null }
    $found = Get-ChildItem -LiteralPath $WebView2 -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
        Sort-Object @{ Expression = { $_.FullName -like "*$hint*" }; Descending = $true }, @{ Expression = { $_.FullName }; Descending = $true }
    if ($found) { return @($found)[0].FullName }
    return $null
}

# Name, sources, and one argument list per run. {app} and {baseline} are filled in.
$tests = @(
    @{ Name = 'Verify';             Sources = @('Verify.cs');                                Runs = @(,@('{baseline}','{app}')); NeedsBaseline = $true },
    @{ Name = 'LegacyActionTests';  Sources = @('LegacyActionTests.cs');                     Runs = @(,@('{app}')) },
    @{ Name = 'NativeDropTests';    Sources = @('NativeDropTests.cs');                       Runs = @(,@('{app}')) },
    @{ Name = 'BrowserDropTests';   Sources = @('BrowserDropTests.cs');                      Runs = @(,@('{app}')) },
    @{ Name = 'ChromiumAttachmentTests'; Sources = @('ChromiumAttachmentTests.cs');          Runs = @(@('{app}'), @('{app}','--background')) },
    @{ Name = 'CompareButtonTests'; Sources = @('CompareButtonTests.cs','DualUploadTests.cs'); Runs = @(@('{app}'), @('{app}','--files')) },
    @{ Name = 'NewChatDropTests';   Sources = @('NewChatDropTests.cs');                      Runs = @(@('{app}','ChatGPT'), @('{app}','Claude')); NeedsWebView2 = $true }
)
if ($Only) { $tests = @($tests | Where-Object { $name = $_.Name; $Only | Where-Object { $name -like "*$_*" } }) }

$results = New-Object System.Collections.ArrayList
function Add-Result([string]$name, [string]$state, [string]$note) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Result = $state; Note = $note })
    Write-Host ("{0,-6} {1}  {2}" -f $state, $name, $note)
}

function Invoke-Test([string]$exe, [string[]]$arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start.WorkingDirectory = $outDir
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            return @{ Code = -1; Text = "timed out after $TimeoutSeconds s" }
        }
        $process.WaitForExit()
        return @{ Code = $process.ExitCode; Text = (($out.Result + $err.Result).Trim()) }
    } finally { $process.Dispose() }
}

foreach ($test in $tests) {
    $exe = Join-Path $outDir ($test.Name + '.exe')
    $extra = @()
    if ($test.NeedsWebView2) {
        $core = Find-WebView2 'Microsoft.Web.WebView2.Core.dll' 'net45'
        $forms = Find-WebView2 'Microsoft.Web.WebView2.WinForms.dll' 'net45'
        $loader = Find-WebView2 'WebView2Loader.dll' 'win-x64'
        if (!$core -or !$forms -or !$loader) {
            Add-Result $test.Name 'SKIP' 'WebView2 package not found; pass -WebView2 <folder of Microsoft.Web.WebView2>'
            continue
        }
        Copy-Item -LiteralPath $core, $forms, $loader -Destination $outDir -Force
        $extra = @('/r:' + $core, '/r:' + $forms)
    }
    $sources = @($test.Sources | ForEach-Object { Join-Path $PSScriptRoot $_ })
    $log = & (Join-Path $frameworkDir 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /unsafe+ ('/out:' + $exe) @references @extra @sources 2>&1
    if ($LASTEXITCODE -ne 0) {
        Add-Result $test.Name 'FAIL' 'did not compile'
        $log | ForEach-Object { Write-Host "       $_" }
        continue
    }
    if ($CompileOnly) { Add-Result $test.Name 'BUILT' ''; continue }
    if ($test.NeedsBaseline -and !(Test-Path -LiteralPath $Baseline)) {
        Add-Result $test.Name 'SKIP' "no earlier build at $Baseline; pass -Baseline <exe>"
        continue
    }
    foreach ($run in $test.Runs) {
        $arguments = @($run | ForEach-Object { $_.Replace('{app}', $App).Replace('{baseline}', $Baseline) })
        $label = $test.Name + (' ' + (($run | Where-Object { $_ -notlike '{*}' }) -join ' ')).TrimEnd()
        $outcome = Invoke-Test $exe $arguments
        $lines = @($outcome.Text -split "`r?`n" | Where-Object { $_ })
        $last = if ($lines.Count) { $lines[-1] } else { '' }
        if ($outcome.Code -eq 0 -and $outcome.Text -notmatch '(?m)^FAIL') { Add-Result $label 'PASS' $last }
        else {
            Add-Result $label 'FAIL' "exit $($outcome.Code)"
            $lines | ForEach-Object { Write-Host "       $_" }
        }
    }
}

$failed = @($results | Where-Object { $_.Result -eq 'FAIL' }).Count
$skipped = @($results | Where-Object { $_.Result -eq 'SKIP' }).Count
Write-Host ''
Write-Host ("{0} run, {1} failed, {2} skipped" -f $results.Count, $failed, $skipped)
if ($failed) { exit 1 }
