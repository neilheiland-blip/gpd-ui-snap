$ErrorActionPreference = "Continue"

$fifaProcessNames = @(
    "FC24",
    "FC25",
    "FC26",
    "FIFA21",
    "FIFA22",
    "FIFA23",
    "FIFA24",
    "FIFA25",
    "FIFA26"
)

$launcherProcessNames = @(
    "XboxPcApp",
    "XboxApp",
    "GameBar",
    "GameBarFTServer",
    "GameBarPresenceWriter"
)

$ahkScripts = @(
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\KeyRemaps.ahk",
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\F1-to-Ctrl-F1.ahk"
)

$motionAssistantProfiles = @(
    "C:\Program Files\Motion Assistant\Profiles\General\default.ini"
)

$logPath = Join-Path $env:LOCALAPPDATA "GpdUiSnap\fifa-autohotkey-watchdog.log"
$motionBackupDirectory = Join-Path $env:LOCALAPPDATA "GpdUiSnap\motion-assistant-backups"
$motionStoppedStatePath = Join-Path $env:LOCALAPPDATA "GpdUiSnap\motion-assistant-stopped.json"
$lastGameState = $null

function Write-WatchdogLog {
    param([string]$Message)

    try {
        $directory = Split-Path -Parent $logPath
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Add-Content -Path $logPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Message"
    } catch {
        # Logging should never stop the watchdog.
    }
}

function Test-FifaRunning {
    foreach ($name in @($fifaProcessNames + $launcherProcessNames)) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
            return $true
        }
    }

    return $false
}

function Get-AutoHotkeyProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "AutoHotkey*" }
}

function Test-AhkScriptRunning {
    param([string]$ScriptPath)

    $fullPath = [System.IO.Path]::GetFullPath($ScriptPath)
    try {
        $processes = Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object { $_.Name -like "AutoHotkey*.exe" -and $_.CommandLine }

        foreach ($process in $processes) {
            if ($process.CommandLine.IndexOf($fullPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }
        }
    } catch {
        # If command-line inspection fails, #SingleInstance in the scripts still prevents duplicates.
        return $false
    }

    return $false
}

function Stop-AutoHotkey {
    $processes = @(Get-AutoHotkeyProcesses)
    if ($processes.Count -eq 0) {
        return
    }

    foreach ($process in $processes) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Write-WatchdogLog "Stopped AutoHotkey process $($process.ProcessName) pid=$($process.Id)."
        } catch {
            Write-WatchdogLog "Failed to stop AutoHotkey pid=$($process.Id): $($_.Exception.Message)"
        }
    }
}

function Start-AutoHotkeyScripts {
    foreach ($script in $ahkScripts) {
        if (-not (Test-Path -LiteralPath $script)) {
            Write-WatchdogLog "AutoHotkey script not found: $script"
            continue
        }

        if (Test-AhkScriptRunning -ScriptPath $script) {
            continue
        }

        try {
            Start-Process -FilePath $script
            Write-WatchdogLog "Started AutoHotkey script: $script"
        } catch {
            Write-WatchdogLog "Failed to start AutoHotkey script '$script': $($_.Exception.Message)"
        }
    }
}

function Get-MotionAssistantProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "MotionAssistant*" -or $_.ProcessName -like "pmgui*" }
}

function Get-MotionBackupPath {
    param([string]$ProfilePath)

    $safeName = ($ProfilePath -replace "[:\\\/]", "_")
    return (Join-Path $motionBackupDirectory "$safeName.bak")
}

function Set-IniValue {
    param(
        [string[]]$Lines,
        [string]$Key,
        [string]$Value
    )

    $found = $false
    $updated = foreach ($line in $Lines) {
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=") {
            $found = $true
            "$Key=$Value"
        } else {
            $line
        }
    }

    if (-not $found) {
        $updated += "$Key=$Value"
    }

    return $updated
}

function Disable-MotionAssistantSettings {
    foreach ($profile in $motionAssistantProfiles) {
        if (-not (Test-Path -LiteralPath $profile)) {
            continue
        }

        try {
            New-Item -ItemType Directory -Path $motionBackupDirectory -Force | Out-Null
            $backupPath = Get-MotionBackupPath -ProfilePath $profile
            if (-not (Test-Path -LiteralPath $backupPath)) {
                Copy-Item -LiteralPath $profile -Destination $backupPath -Force
                Write-WatchdogLog "Backed up Motion Assistant profile: $profile"
            }

            $lines = [System.IO.File]::ReadAllLines($profile)
            $lines = Set-IniValue -Lines $lines -Key "FanControlEnable" -Value "False"
            $lines = Set-IniValue -Lines $lines -Key "ACTDP" -Value "0"
            $lines = Set-IniValue -Lines $lines -Key "DCTDP" -Value "0"
            $lines = Set-IniValue -Lines $lines -Key "AutoSetTDP" -Value "False"
            $lines = Set-IniValue -Lines $lines -Key "UnitedTDP" -Value "False"
            $lines = Set-IniValue -Lines $lines -Key "CustomTDP1" -Value "0"
            $lines = Set-IniValue -Lines $lines -Key "CustomTDP2" -Value "0"
            $lines = Set-IniValue -Lines $lines -Key "CustomTDP3" -Value "0"
            $lines = Set-IniValue -Lines $lines -Key "useCustomGPU" -Value "False"
            $lines = Set-IniValue -Lines $lines -Key "AutoLockGPU" -Value "False"
            $lines = Set-IniValue -Lines $lines -Key "optimizeGPU" -Value "False"
            [System.IO.File]::WriteAllLines($profile, $lines)
            Write-WatchdogLog "Disabled Motion Assistant fan/TDP settings: $profile"
        } catch {
            Write-WatchdogLog "Failed to disable Motion Assistant profile '$profile': $($_.Exception.Message)"
        }
    }
}

function Restore-MotionAssistantSettings {
    foreach ($profile in $motionAssistantProfiles) {
        $backupPath = Get-MotionBackupPath -ProfilePath $profile
        if (-not (Test-Path -LiteralPath $backupPath)) {
            continue
        }

        try {
            Copy-Item -LiteralPath $backupPath -Destination $profile -Force
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
            Write-WatchdogLog "Restored Motion Assistant profile: $profile"
        } catch {
            Write-WatchdogLog "Failed to restore Motion Assistant profile '$profile': $($_.Exception.Message)"
        }
    }
}

function Stop-MotionAssistantControls {
    $processes = @(Get-MotionAssistantProcesses)
    if ($processes.Count -eq 0) {
        return
    }

    $paths = @()
    foreach ($process in $processes) {
        if ($process.Path -and (Test-Path -LiteralPath $process.Path)) {
            $paths += $process.Path
        }

        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Write-WatchdogLog "Stopped Motion Assistant process $($process.ProcessName) pid=$($process.Id)."
        } catch {
            Write-WatchdogLog "Failed to stop Motion Assistant pid=$($process.Id): $($_.Exception.Message)"
        }
    }

    if ($paths.Count -gt 0) {
        try {
            $paths | Select-Object -Unique | ConvertTo-Json | Set-Content -Path $motionStoppedStatePath
        } catch {
            Write-WatchdogLog "Failed to save Motion Assistant restart state: $($_.Exception.Message)"
        }
    }
}

function Start-MotionAssistantControls {
    if (-not (Test-Path -LiteralPath $motionStoppedStatePath)) {
        return
    }

    try {
        $paths = @(Get-Content -LiteralPath $motionStoppedStatePath -Raw | ConvertFrom-Json)
        foreach ($path in $paths) {
            if ($path -and (Test-Path -LiteralPath $path)) {
                Start-Process -FilePath $path
                Write-WatchdogLog "Restarted Motion Assistant: $path"
            }
        }

        Remove-Item -LiteralPath $motionStoppedStatePath -Force -ErrorAction SilentlyContinue
    } catch {
        Write-WatchdogLog "Failed to restart Motion Assistant: $($_.Exception.Message)"
    }
}

Write-WatchdogLog "FIFA AutoHotkey watchdog started."

while ($true) {
    $fifaRunning = Test-FifaRunning

    if ($fifaRunning) {
        if ($lastGameState -ne $true) {
            Write-WatchdogLog "FIFA/EA Sports FC or Xbox launcher detected. Disabling AutoHotkey."
        }

        Stop-AutoHotkey
        Disable-MotionAssistantSettings
        Stop-MotionAssistantControls
    } else {
        if ($lastGameState -ne $false) {
            Write-WatchdogLog "FIFA/EA Sports FC and Xbox launcher not running. Enabling AutoHotkey."
        }

        Restore-MotionAssistantSettings
        Start-MotionAssistantControls
        Start-AutoHotkeyScripts
    }

    $lastGameState = $fifaRunning
    Start-Sleep -Seconds 3
}
