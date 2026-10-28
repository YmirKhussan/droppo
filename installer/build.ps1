# Builds dist\Droppo-Setup-<version>.exe: one file anyone can install, nothing else needed.
# Run it through build-installer.bat. Safe to run again; downloads are cached in dist\downloads.
#   -Fresh   download FFmpeg again (to pick up a newer version)

#   -NoOpen  don't open the folder at the end (used by the GitHub build)

param([switch]$Fresh, [switch]$NoOpen)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads 10x slower in Windows PowerShell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root      = Split-Path $PSScriptRoot -Parent
$project   = Join-Path $root 'src\Droppo\Droppo.csproj'
$dist      = Join-Path $root 'dist'
$stage     = Join-Path $dist 'stage'
$downloads = Join-Path $dist 'downloads'

function Step($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host ""; Write-Host "FAILED: $text" -ForegroundColor Red; exit 1 }

function Refresh-Path {
    # pick up tools winget just installed without reopening the window
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Winget-Install($id) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Fail "winget isn't available. Install '$id' by hand, then run this again." }
    Write-Host "Installing $id with winget..."
    winget install --id $id -e --accept-source-agreements --accept-package-agreements
    Refresh-Path
}

function Find-Iscc {
    $roots = @("$env:LOCALAPPDATA\Programs", ${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ }
    foreach ($r in $roots) {
        $hit = Get-ChildItem -Path $r -Filter 'Inno Setup*' -Directory -ErrorAction SilentlyContinue |
               Sort-Object Name -Descending |
               ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
               Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

function Find-Qpdf {
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs") | Where-Object { $_ }
    foreach ($r in $roots) {
        $hit = Get-ChildItem -Path $r -Filter 'qpdf*' -Directory -ErrorAction SilentlyContinue |
               Sort-Object Name -Descending |
               ForEach-Object { Join-Path $_.FullName 'bin\qpdf.exe' } |
               Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    $cmd = Get-Command qpdf -ErrorAction SilentlyContinue
    if ($cmd) {
        $item = Get-Item $cmd.Source
        if ($item.LinkType -and $item.Target) { return [string]($item.Target | Select-Object -First 1) }
        return $cmd.Source
    }
    return $null
}

function Download($url, $file) {
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing -Headers @{ 'User-Agent' = 'Droppo-build' }
}

# ---------------------------------------------------------------- tools to build with
Step 'Checking build tools'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Winget-Install 'Microsoft.DotNet.SDK.8' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail '.NET SDK still not found. Close this window, open a new one and try again.' }
$iscc = Find-Iscc
if (-not $iscc) { Winget-Install 'JRSoftware.InnoSetup'; $iscc = Find-Iscc }
if (-not $iscc) { Fail 'Inno Setup (the installer maker) not found after installing it. Try: winget install JRSoftware.InnoSetup' }
Write-Host "dotnet:     $((Get-Command dotnet).Source)"
Write-Host "Inno Setup: $iscc"

[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '0.0.0' }
Write-Host "Version:    $version"

# ---------------------------------------------------------------- the app
Step 'Building Droppo (self-contained: people do not need .NET installed)'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage, $downloads | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { Fail 'The build failed. The error is above.' }
if (-not (Test-Path (Join-Path $stage 'Droppo.exe'))) { Fail 'Build finished but Droppo.exe is missing.' }

$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') $stage

# ---------------------------------------------------------------- FFmpeg (pictures, video, audio)
Step 'FFmpeg'
$ffDir = Join-Path $stage 'tools\ffmpeg'
New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
$ffZip = Join-Path $downloads 'ffmpeg-release-essentials.zip'
if ($Fresh -and (Test-Path $ffZip)) { Remove-Item $ffZip -Force }

if (-not (Test-Path $ffZip)) {
    try {
        Download 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' $ffZip
        try {
            $shaFile = "$ffZip.sha256"
            Download 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256' $shaFile
            $expected = ((Get-Content $shaFile -Raw).Trim() -split '\s+')[0]
            $actual = (Get-FileHash $ffZip -Algorithm SHA256).Hash
            if ($expected -and ($expected -ne $actual)) { Remove-Item $ffZip -Force; Fail 'FFmpeg download is corrupted (checksum mismatch). Run again.' }
            Write-Host 'Checksum OK'
        } catch { Write-Host "Couldn't check the checksum, continuing: $($_.Exception.Message)" -ForegroundColor Yellow }
    } catch {
        if (Test-Path $ffZip) { Remove-Item $ffZip -Force }
        Write-Host "Download failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if (Test-Path $ffZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ffZip)
    try {
        $exe = $zip.Entries | Where-Object { $_.FullName -like '*/bin/ffmpeg.exe' } | Select-Object -First 1
        if (-not $exe) { Fail 'ffmpeg.exe not found inside the FFmpeg download.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($exe, (Join-Path $ffDir 'ffmpeg.exe'), $true)
        $lic = $zip.Entries | Where-Object { $_.Name -match '^(LICENSE|COPYING)' -and ($_.FullName.Split('/').Count -le 2) } | Select-Object -First 1
        if ($lic) { [IO.Compression.ZipFileExtensions]::ExtractToFile($lic, (Join-Path $licenses 'FFmpeg-LICENSE.txt'), $true) }
    } finally { $zip.Dispose() }
} else {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if (-not $cmd) { Fail "Couldn't download FFmpeg and it isn't installed either. Check the internet connection, or run: winget install Gyan.FFmpeg" }
    Write-Host "Using the FFmpeg installed on this PC: $($cmd.Source)" -ForegroundColor Yellow
    Copy-Item $cmd.Source (Join-Path $ffDir 'ffmpeg.exe')
}

$ff = Join-Path $ffDir 'ffmpeg.exe'
$encoders = & $ff -hide_banner -encoders | Out-String
foreach ($need in 'libx264', 'libmp3lame', 'libwebp') {
    if ($encoders -notmatch $need) { Write-Host "WARNING: this FFmpeg has no $need, some actions will fail." -ForegroundColor Yellow }
}
Write-Host ((& $ff -hide_banner -version | Select-Object -First 1))

# ---------------------------------------------------------------- qpdf (PDF tools)
Step 'qpdf'
$qpdf = Find-Qpdf
if (-not $qpdf) { Winget-Install 'QPDF.QPDF'; $qpdf = Find-Qpdf }
if (-not $qpdf) { Fail 'qpdf not found after installing it. Try: winget install QPDF.QPDF' }
$qBin = Split-Path $qpdf -Parent
if ((Split-Path $qBin -Leaf) -eq 'Links') { Fail "qpdf at $qpdf is only a shortcut. Reinstall it with: winget install QPDF.QPDF" }
Write-Host "Using $qpdf"
$qDir = Join-Path $stage 'tools\qpdf'
New-Item -ItemType Directory -Force -Path $qDir | Out-Null
Copy-Item (Join-Path $qBin 'qpdf.exe') $qDir
Get-ChildItem $qBin -Filter '*.dll' | Copy-Item -Destination $qDir
$qRoot = Split-Path $qBin -Parent
Get-ChildItem $qRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(LICENSE|NOTICE|Artistic|COPYING)' } | Select-Object -First 4 |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $licenses ("qpdf-" + $_.Name)) -Force }
$qv = & (Join-Path $qDir 'qpdf.exe') --version
if ($LASTEXITCODE -ne 0) { Fail 'The copied qpdf does not run (a DLL is probably missing).' }
Write-Host ($qv | Select-Object -First 1)

# ---------------------------------------------------------------- installer
Step 'Making the installer'
& $iscc /Q "/DAppVersion=$version" "/DStageDir=$stage" "/DOutputDir=$dist" (Join-Path $PSScriptRoot 'Droppo.iss')
if ($LASTEXITCODE -ne 0) { Fail 'Inno Setup failed. The error is above.' }

$setup = Join-Path $dist "Droppo-Setup-$version.exe"
$mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $setup ($mb MB)" -ForegroundColor Green
Write-Host "Share that one file. People double-click it and that's it."
if (-not $NoOpen) { Start-Process explorer.exe "/select,`"$setup`"" }
