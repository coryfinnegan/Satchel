# Compiles installer\Satchel.iss into Satchel-Setup-v<Version>.exe.
#   .\installer\build-installer.ps1 -Version 1.2.3 -SourceDir out\self-contained -OutputDir dist
# Needs Inno Setup 6.3+ (ISCC.exe). With -InstallInnoIfMissing it installs it via Chocolatey (used on CI runners).
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$SourceDir,
    [Parameter(Mandatory)] [string]$OutputDir,
    [switch]$InstallInnoIfMissing
)
$ErrorActionPreference = "Stop"

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { $found = (Get-Command iscc -ErrorAction SilentlyContinue).Source }
    return $found
}

$iscc = Find-Iscc
if (-not $iscc -and $InstallInnoIfMissing) {
    choco install innosetup -y --no-progress | Out-Host
    $iscc = Find-Iscc
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php or run with -InstallInnoIfMissing." }

$source = (Resolve-Path $SourceDir).Path
if (-not (Test-Path (Join-Path $source "Satchel.exe"))) { throw "No Satchel.exe in $source" }
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$output = (Resolve-Path $OutputDir).Path
# Windows file versions must be numeric: 1.2.3-beta.1 -> 1.2.3
$numeric = ($Version -split '-')[0]

& $iscc "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DSourceDir=$source" "/DOutputDir=$output" "/Qp" (Join-Path $PSScriptRoot "Satchel.iss")
if ($LASTEXITCODE) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $output "Satchel-Setup-v$Version.exe"
if (-not (Test-Path $setup)) { throw "Expected $setup" }
"Built $setup ({0:N1} MB)" -f ((Get-Item $setup).Length / 1MB)
