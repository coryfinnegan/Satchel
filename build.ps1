# Builds a release copy of Satchel into .\dist\Satchel
#   .\build.ps1                 framework-dependent (needs the .NET 10 Desktop Runtime, ~3 MB)
#   .\build.ps1 -SelfContained  bundles .NET (no runtime needed, ~70 MB)
param([switch]$SelfContained)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\Satchel"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

$sc = if ($SelfContained) { "true" } else { "false" }
dotnet publish (Join-Path $root "src\Satchel\Satchel.csproj") -c Release -r win-x64 `
    --self-contained $sc -p:PublishSingleFile=true -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Get-ChildItem $out | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
"Built: $out\Satchel.exe"
