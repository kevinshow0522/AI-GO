$ErrorActionPreference = 'Stop'
$installPath = Join-Path $env:LOCALAPPDATA 'Programs\AI Switch Desk\AISwitchDesk.exe'
$sourcePath = Join-Path $PSScriptRoot 'build\AISwitchDesk.exe'
$session = (Get-Process -Id $PID).SessionId
$mutex = [Threading.Mutex]::new($false, 'Local\AISwitchDesk.Deploy.v1')
$locked = $false

function Get-InstalledProcesses {
    Get-CimInstance Win32_Process -Filter "Name = 'AISwitchDesk.exe' AND SessionId = $session" |
        Where-Object { $_.ExecutablePath -eq $installPath }
}

# The interactive sidebar: started with no arguments, or with --tray at sign-in.
# The background worker and the MCP servers run the same file with other arguments.
function Test-Sidebar($process) {
    $line = $process.CommandLine.Trim()
    $rest = if ($line.StartsWith('"')) { $line.Substring($line.IndexOf('"', 1) + 1) } else { $line.Substring([Math]::Min($line.Length, $installPath.Length)) }
    $rest = $rest.Trim()
    return $rest -eq '' -or $rest -eq '--tray'
}

function Stop-Sidebar {
    foreach ($app in (Get-InstalledProcesses | Where-Object { Test-Sidebar $_ })) {
        $process = Get-Process -Id $app.ProcessId -ErrorAction SilentlyContinue
        if ($process -and !$process.HasExited) {
            # WM_CLOSE lets the sidebar checkpoint uploads and restore compared windows.
            if (!$process.CloseMainWindow() -or !$process.WaitForExit(15000)) {
                throw 'The sidebar did not finish closing.'
            }
        }
    }
}

function Stop-Background {
    $workers = @(Get-InstalledProcesses | Where-Object { $_.CommandLine -match '\s--background\s*$' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    if (!$workers.Count) { return }
    $stop = [Threading.EventWaitHandle]::OpenExisting('Local\AISwitchDesk.Background.Stop.v1')
    try { [void]$stop.Set() } finally { $stop.Dispose() }
    foreach ($worker in $workers) {
        if (!$worker.WaitForExit(45000)) { throw 'The current background delivery is still finishing.' }
    }
}

function Start-Sidebar {
    # The sidebar is the installed interactive UI. It starts the background worker itself.
    Start-Process -FilePath $installPath -WindowStyle Normal
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $apps = @(Get-InstalledProcesses)
        $ui = $apps | Where-Object { Test-Sidebar $_ } | Select-Object -First 1
        if ($ui -and ($apps | Where-Object { $_.CommandLine -match '\s--background\s*$' })) {
            $window = Get-Process -Id $ui.ProcessId -ErrorAction SilentlyContinue
            if ($window -and $window.MainWindowHandle -ne 0 -and $window.Responding) { return }
        }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 20)
    throw 'The updated sidebar and background worker did not both start.'
}

# The Start menu keeps the picture it took when a shortcut was made, so a new
# app icon does not show there. Saving the shortcut again and telling the shell
# that icons changed makes it take the installed app's current one.
function Update-ShortcutIcons {
    $programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    if (!(Test-Path -LiteralPath $programs)) { return }
    $shell = New-Object -ComObject WScript.Shell
    $saved = 0
    foreach ($file in (Get-ChildItem -LiteralPath $programs -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue)) {
        $link = $shell.CreateShortcut($file.FullName)
        if ($link.TargetPath -ne $installPath) { continue }
        $link.IconLocation = "$installPath,0"
        $link.Save()
        $saved++
    }
    if (!$saved) { return }
    Add-Type -Namespace AISwitchDeskDeploy -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);'
    [AISwitchDeskDeploy.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
    Write-Output ("Refreshed the icon of $saved Start menu shortcut(s)")
}

try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Another update is already running.' }
    $oldVersion = (Get-Item -LiteralPath $installPath).VersionInfo.FileVersion
    $oldHash = (Get-FileHash -LiteralPath $installPath).Hash
    $newHash = (Get-FileHash -LiteralPath $sourcePath).Hash
    $changed = $oldHash -ne $newHash
    $directory = Split-Path -Parent $installPath
    $tag = [guid]::NewGuid().ToString('N')
    $stage = Join-Path $directory ("AISwitchDesk-$tag.staged.exe")
    $backup = Join-Path $directory ("AISwitchDesk-$oldVersion.previous.exe")
    if (Test-Path -LiteralPath $backup) {
        $backup = Join-Path $directory ("AISwitchDesk-$oldVersion-$tag.previous.exe")
    }
    $restart = $false
    try {
        if ($changed) {
            [IO.File]::Copy($sourcePath, $stage)
            if ((Get-FileHash -LiteralPath $stage).Hash -ne $newHash) { throw 'Staging hash mismatch.' }
        }
        $restart = $true
        Stop-Sidebar
        if ($changed) {
            try { [IO.File]::Replace($stage, $installPath, $backup) }
            catch [IO.IOException] {
                # Windows can rename a mapped MCP executable even when ReplaceFile
                # cannot replace it. Keep its image backed by the previous file.
                if (!(Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $backup) -or
                    (Get-FileHash -LiteralPath $installPath).Hash -ne $oldHash) { throw }
                [IO.File]::Move($installPath, $backup)
                [IO.File]::Move($stage, $installPath)
            }
            if ((Get-FileHash -LiteralPath $installPath).Hash -ne $newHash -or
                (Get-FileHash -LiteralPath $backup).Hash -ne $oldHash) { throw 'Installation hash mismatch.' }
        }
        # Stop the mapped worker after replacement. Subsequent launches see the new file.
        Stop-Background
        Start-Sidebar
        Write-Output ('Installed ' + (Get-Item -LiteralPath $installPath).VersionInfo.FileVersion)
        if ($changed) { Write-Output ('Backup ' + $backup) }
        # A stale icon is cosmetic. It must not undo an installation that worked.
        try { Update-ShortcutIcons } catch { Write-Output ('Shortcut icons not refreshed: ' + $_.Exception.Message) }
    } catch {
        $failure = $_
        if ($changed -and (Test-Path -LiteralPath $backup)) {
            Stop-Sidebar
            if ((Get-FileHash -LiteralPath $backup).Hash -ne $oldHash) { throw 'Previous executable could not be verified.' }
            if (Test-Path -LiteralPath $installPath) {
                [IO.File]::Move($installPath, (Join-Path $directory ("AISwitchDesk-$tag.failed.exe")))
            }
            [IO.File]::Move($backup, $installPath)
            Stop-Background
        }
        if ($restart) { Start-Sidebar }
        throw $failure
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage }
    }
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
