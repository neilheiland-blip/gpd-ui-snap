$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Startup = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $Startup 'GPD UI Snap.lnk'
$Exe = Join-Path $Root 'bin\Release\net8.0-windows\win-x64\publish\GpdUiSnap.exe'

if (!(Test-Path $Exe)) {
    throw "Published app not found: $Exe. Run publish-release.ps1 first."
}

$Shell = New-Object -ComObject WScript.Shell
$Shortcut = $Shell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $Exe
$Shortcut.WorkingDirectory = Split-Path -Parent $Exe
$Shortcut.WindowStyle = 7
$Shortcut.Description = 'GPD UI Snap'
$Shortcut.Save()

Write-Host "Installed startup shortcut: $ShortcutPath"
