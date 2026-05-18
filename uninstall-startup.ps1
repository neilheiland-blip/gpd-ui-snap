$ErrorActionPreference = 'Stop'

$Startup = [Environment]::GetFolderPath('Startup')
$ShortcutPath = Join-Path $Startup 'GPD UI Snap.lnk'

if (Test-Path $ShortcutPath) {
    Remove-Item $ShortcutPath -Force
    Write-Host "Removed startup shortcut: $ShortcutPath"
} else {
    Write-Host "Startup shortcut was not installed."
}
