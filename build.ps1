<#
.SYNOPSIS
    CupHeads build + package script.

.DESCRIPTION
    1. Locates the Cuphead install (Steam registry or CUPHEAD_PATH env-var)
    2. Builds the mod DLL (CupheadOnline.csproj)
    3. Copies the DLL into ./dist/
    4. Stages the DLL into CupheadInstaller/assets/
    5. Builds the Electron installer
    6. Copies the portable installer into ./dist/
    7. Optionally deploys the mod into BepInEx/plugins/CupheadOnline

.USAGE
    .\build.ps1
    .\build.ps1 -Release
    .\build.ps1 -NoDeploy
    .\build.ps1 -InstallNodeModules
#>
param(
    [string] $CupheadPath = $env:CUPHEAD_PATH,
    [switch] $NoDeploy,
    [switch] $Release,
    [switch] $InstallNodeModules
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Configuration      = if ($Release) { "Release" } else { "Debug" }
$Root               = $PSScriptRoot
$DistDir            = Join-Path $Root "dist"
$ModProject         = Join-Path $Root "CupheadOnline\CupheadOnline.csproj"
$InstallerDir       = Join-Path $Root "CupheadInstaller"
$InstallerAssetsDir = Join-Path $InstallerDir "assets"
$InstallerExe       = Join-Path $InstallerDir "dist\CupHeads.exe"
$BepInExVersion     = "5.4.23.5"
$BepInExPackages    = @(
    @{
        Arch = "x64"
        FileName = "BepInEx_win_x64_$BepInExVersion.zip"
        Url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
    },
    @{
        Arch = "x86"
        FileName = "BepInEx_win_x86_$BepInExVersion.zip"
        Url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x86_$BepInExVersion.zip"
    }
)

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  >>> $msg" -ForegroundColor Cyan
}

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "  FATAL: $msg" -ForegroundColor Red
    exit 1
}

function Invoke-Checked([scriptblock]$Action, [string]$ErrorMessage) {
    & $Action
    if ($LASTEXITCODE -ne 0) { Fail $ErrorMessage }
}

function Ensure-BepInExBundle([string]$AssetsDir) {
    $keep = @($BepInExPackages | ForEach-Object { $_.FileName })

    Get-ChildItem $AssetsDir -Filter "BepInEx*.zip" -File -ErrorAction SilentlyContinue |
        Where-Object { $keep -notcontains $_.Name } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    foreach ($pkg in $BepInExPackages) {
        $destination = Join-Path $AssetsDir $pkg.FileName
        if (Test-Path $destination) {
            Write-Host "  Using cached $($pkg.FileName)" -ForegroundColor Green
            continue
        }

        Write-Host "  Downloading $($pkg.FileName)" -ForegroundColor Cyan
        Invoke-WebRequest `
            -Uri $pkg.Url `
            -Headers @{ "User-Agent" = "CupHeads-Build" } `
            -OutFile $destination
    }
}

Write-Host ""
Write-Host "  +====================================+" -ForegroundColor Yellow
Write-Host "  |      CupHeads Build Script         |" -ForegroundColor Yellow
Write-Host "  +====================================+" -ForegroundColor Yellow
Write-Host ""

Write-Step "Locating Cuphead"

if (-not $CupheadPath) {
    $steamKey = "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam"
    if (-not (Test-Path $steamKey)) {
        $steamKey = "HKLM:\SOFTWARE\Valve\Steam"
    }
    if (Test-Path $steamKey) {
        $steamPath = (Get-ItemProperty $steamKey).InstallPath
        $candidate = Join-Path $steamPath "steamapps\common\Cuphead"
        if (Test-Path "$candidate\Cuphead.exe") { $CupheadPath = $candidate }
    }
}

if (-not $CupheadPath) {
    $fallbacks = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Cuphead",
        "C:\Program Files\Steam\steamapps\common\Cuphead",
        "D:\SteamLibrary\steamapps\common\Cuphead",
        "D:\Steam\steamapps\common\Cuphead",
        "E:\SteamLibrary\steamapps\common\Cuphead"
    )
    foreach ($fb in $fallbacks) {
        if (Test-Path "$fb\Cuphead.exe") {
            $CupheadPath = $fb
            break
        }
    }
}

if (-not $CupheadPath -or -not (Test-Path "$CupheadPath\Cuphead.exe")) {
    Write-Host "  Could not auto-detect Cuphead." -ForegroundColor Yellow
    $CupheadPath = Read-Host "  Enter full path to Cuphead folder"
    if (-not (Test-Path "$CupheadPath\Cuphead.exe")) {
        Fail "Cuphead.exe not found at: $CupheadPath"
    }
}

Write-Host "  Found: $CupheadPath" -ForegroundColor Green
$env:CUPHEAD_PATH = $CupheadPath

Write-Step "Restoring NuGet packages"
Invoke-Checked { dotnet restore $ModProject --nologo } "NuGet restore failed."

Write-Step "Building mod DLL ($Configuration)"
Invoke-Checked { dotnet build $ModProject -c $Configuration --nologo --no-restore } "Mod build failed."

$ModOutput = Join-Path $Root "CupheadOnline\bin\$Configuration\net35"
$ModDll    = Join-Path $ModOutput "CupheadOnline.dll"
if (-not (Test-Path $ModDll)) {
    Fail "CupheadOnline.dll not found after build."
}

Write-Step "Preparing dist/"
New-Item -ItemType Directory -Force $DistDir | Out-Null
Remove-Item (Join-Path $DistDir "LiteNetLib.dll") -Force -ErrorAction SilentlyContinue
Copy-Item $ModDll (Join-Path $DistDir "CupheadOnline.dll") -Force

Write-Step "Staging DLL for Electron installer"
New-Item -ItemType Directory -Force $InstallerAssetsDir | Out-Null
Copy-Item $ModDll (Join-Path $InstallerAssetsDir "CupheadOnline.dll") -Force

Write-Step "Bundling BepInEx repair packages"
Ensure-BepInExBundle -AssetsDir $InstallerAssetsDir

if ($InstallNodeModules -or -not (Test-Path (Join-Path $InstallerDir "node_modules"))) {
    Write-Step "Installing Node dependencies"
    Push-Location $InstallerDir
    try {
        Invoke-Checked { npm.cmd install } "npm install failed."
    }
    finally {
        Pop-Location
    }
}

Write-Step "Building Electron installer"
Push-Location $InstallerDir
try {
    Invoke-Checked { npm.cmd run dist } "Electron installer build failed."
}
finally {
    Pop-Location
}

if (-not (Test-Path $InstallerExe)) {
    Fail "Portable installer not found at: $InstallerExe"
}

Remove-Item (Join-Path $DistDir "Cupheads.exe") -Force -ErrorAction SilentlyContinue
Copy-Item $InstallerExe (Join-Path $DistDir "CupHeads.exe") -Force

if (-not $NoDeploy) {
    Write-Step "Deploying to BepInEx plugin folder"
    $PluginDir = Join-Path $CupheadPath "BepInEx\plugins\CupheadOnline"
    New-Item -ItemType Directory -Force $PluginDir | Out-Null
    Remove-Item (Join-Path $PluginDir "LiteNetLib.dll") -Force -ErrorAction SilentlyContinue
    Copy-Item $ModDll (Join-Path $PluginDir "CupheadOnline.dll") -Force

    $StartupSplash = Join-Path $InstallerAssetsDir "StartupSplash\CupHeadsIntro.mp4"
    if (Test-Path $StartupSplash) {
        $PluginAssetsDir = Join-Path $PluginDir "Assets"
        New-Item -ItemType Directory -Force $PluginAssetsDir | Out-Null
        Copy-Item $StartupSplash (Join-Path $PluginAssetsDir "CupHeadsIntro.mp4") -Force
        Write-Host "  Deployed startup splash video." -ForegroundColor Green
    }

    Write-Host "  Deployed to: $PluginDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "  +==============================================+" -ForegroundColor Green
Write-Host "  | Build complete                               |" -ForegroundColor Green
Write-Host "  |                                              |" -ForegroundColor Green
Write-Host "  | dist\\CupheadOnline.dll  <- mod              |" -ForegroundColor Green
Write-Host "  | dist\\CupHeads.exe      <- web installer    |" -ForegroundColor Green
Write-Host "  +==============================================+" -ForegroundColor Green
Write-Host ""
