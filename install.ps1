# Installs the built app for the current user (no admin needed):
#   copies dist\Satchel to %LOCALAPPDATA%\Programs\Satchel and adds a Start menu shortcut.
#   .\install.ps1 -Desktop     also adds a desktop shortcut
#   .\install.ps1 -Uninstall   removes the app and shortcuts (your settings in %APPDATA%\Satchel are kept)
param([switch]$Desktop, [switch]$Uninstall)
$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\Satchel"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Satchel.lnk"
$desktopLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Satchel.lnk"

Get-Process Satchel -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force

if ($Uninstall) {
    foreach ($p in @($startMenu, $desktopLnk)) { if (Test-Path $p) { Remove-Item $p -Force } }
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    $run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    if (Get-ItemProperty -Path $run -Name Satchel -ErrorAction SilentlyContinue) { Remove-ItemProperty -Path $run -Name Satchel }
    "Satchel removed. Settings remain in $env:APPDATA\Satchel (delete that folder to forget everything)."
    return
}

$source = Join-Path $PSScriptRoot "dist\Satchel"
if (-not (Test-Path (Join-Path $source "Satchel.exe"))) { throw "Run .\build.ps1 first." }
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $target -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$links = @($startMenu)
if ($Desktop) { $links += $desktopLnk }
foreach ($lnk in $links) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $target "Satchel.exe"
    $s.WorkingDirectory = $target
    $s.Description = "Ad-free World of Warcraft addon manager"
    $s.Save()
}
"Installed to $target"
"Start menu shortcut: $startMenu"
