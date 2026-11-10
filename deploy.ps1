$ErrorActionPreference = 'Stop'
$installPath = Join-Path $env:LOCALAPPDATA 'Programs\AI Switch Desk\AISwitchDesk.exe'
$sourcePath = Join-Path $PSScriptRoot 'build\AISwitchDesk.exe'
$oldVersion = (Get-Item -LiteralPath $installPath).VersionInfo.FileVersion
$backupPath = Join-Path (Split-Path -Parent $installPath) ('AISwitchDesk-' + $oldVersion + '.previous.exe')
if (Test-Path -LiteralPath $backupPath) { throw ('Existing backup preserved: ' + $backupPath) }
Get-CimInstance Win32_Process -Filter "Name = 'AISwitchDesk.exe'" |
    Where-Object { $_.ExecutablePath -eq $installPath -and $_.CommandLine -notmatch '--' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId }
Move-Item -LiteralPath $installPath -Destination $backupPath
try {
    Copy-Item -LiteralPath $sourcePath -Destination $installPath
    if ((Get-FileHash -LiteralPath $sourcePath).Hash -ne (Get-FileHash -LiteralPath $installPath).Hash) { throw 'Installed hash mismatch.' }
} catch {
    if (Test-Path -LiteralPath $installPath) { Remove-Item -LiteralPath $installPath }
    Move-Item -LiteralPath $backupPath -Destination $installPath
    throw
}
# This is the interactive sidebar the user operates.
Start-Process -FilePath $installPath -WindowStyle Normal
Write-Output ('Installed ' + (Get-Item -LiteralPath $installPath).VersionInfo.FileVersion)
