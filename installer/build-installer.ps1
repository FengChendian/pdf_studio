#requires -Version 5.1
<#
.SYNOPSIS
    Builds the PDF Studio NSIS installer.

.DESCRIPTION
    Reads the version from Package.appxmanifest (<Identity Version="..."/>)
    and invokes makensis with /DVERSION so the installer is built with the
    matching version number.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Verbosity V2
#>
[CmdletBinding()]
param(
    [ValidateSet('V0','V1','V2','V3','V4')]
    [string]$Verbosity = 'V2'
)

$ErrorActionPreference = 'Stop'

$scriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot  = Split-Path -Parent $scriptDir
$manifestPath = Join-Path $projectRoot 'Package.appxmanifest'
$nsiPath      = Join-Path $scriptDir 'pdf_studio.nsi'

# --- Read version from Package.appxmanifest -------------------------------
if (-not (Test-Path $manifestPath)) {
    throw "Package.appxmanifest not found: $manifestPath"
}

[xml]$manifest = Get-Content $manifestPath -Raw
$version = $manifest.Package.Identity.Version

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not find <Identity Version=""...""/> in $manifestPath"
}

# NSIS VIProductVersion requires 4 numeric parts (e.g. 1.0.0.0)
if ($version -notmatch '^\d+(\.\d+){3}$') {
    Write-Warning "Version '$version' is not in x.y.z.w format; NSIS version info may be limited."
}

Write-Host "Version from Package.appxmanifest: $version" -ForegroundColor Cyan

# --- Locate makensis -------------------------------------------------------
$makensis = Get-Command makensis -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $makensis) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "$env:ProgramFiles\NSIS\makensis.exe"
    )
    $makensis = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $makensis) {
    throw "makensis.exe not found. Please install NSIS (https://nsis.sourceforge.net/) first."
}

Write-Host "Using makensis: $makensis" -ForegroundColor Cyan

# --- Build ------------------------------------------------------------------
Push-Location $scriptDir
try {
    & $makensis "/$Verbosity" "/DVERSION=$version" $nsiPath
    if ($LASTEXITCODE -ne 0) {
        throw "makensis failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$installer = Join-Path $scriptDir "PDFStudio-Setup-$version.exe"
if (Test-Path $installer) {
    $sizeMB = [math]::Round((Get-Item $installer).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Build succeeded: $installer ($sizeMB MB)" -ForegroundColor Green
}
