<#
.SYNOPSIS
Builds the Windows distribution of tramoya: windows\dist\Tramoya\ and the zip
windows\dist\Tramoya-<version>-windows.zip.

.DESCRIPTION
Publishes Tramoya.App (Release, win-x64, self-contained, single file), copies
ffmpeg.exe and ffprobe.exe into dist\Tramoya\ffmpeg\, downloads Chromium into
dist\Tramoya\navegador\ with Playwright, writes LEEME.txt and zips the folder.
Code signing happens outside this script (signtool).

Works on Windows PowerShell 5.1.

.PARAMETER Ffmpeg
Folder that holds ffmpeg.exe and ffprobe.exe. Otherwise $env:TRAMOYA_FFMPEG,
otherwise the folder of the ffmpeg found in PATH.

.PARAMETER SkipBrowser
Do not download Chromium (fast builds while developing).

.PARAMETER Navegador
A folder that already holds the Playwright browsers this build needs (for example
%LOCALAPPDATA%\Tramoya
avegador after a first run): the newest chromium-*,
chromium_headless_shell-*, winldd-* and ffmpeg-* are copied instead of downloaded.
#>
[CmdletBinding()]
param(
    [string]$Ffmpeg,
    [switch]$SkipBrowser,
    [string]$Navegador
)

$ErrorActionPreference = 'Stop'

$windows = $PSScriptRoot
$distRoot = Join-Path $windows 'dist'
$dist = Join-Path $distRoot 'Tramoya'
$csproj = Join-Path $windows 'Tramoya.App\Tramoya.App.csproj'

# dotnet: the one in PATH, or the standard install.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No encuentro dotnet (ni en el PATH ni en C:\Program Files\dotnet).'
}

# Version from Directory.Build.props, the single place where it lives.
$props = Get-Content (Join-Path $windows 'Directory.Build.props') -Raw
if ($props -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
    throw 'Directory.Build.props no tiene <Version>.'
}
$version = $Matches[1]

Write-Host "== Tramoya ${version}: dotnet publish (Release, win-x64, un solo fichero) =="
if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force $dist | Out-Null
& dotnet publish $csproj -c Release -r win-x64 -o $dist
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish ha fallado (codigo $LASTEXITCODE)."
}

# --- ffmpeg ---------------------------------------------------------------------
Write-Host '== ffmpeg =='
$ffmpegDir = $null
if ($Ffmpeg) {
    $ffmpegDir = $Ffmpeg
} elseif ($env:TRAMOYA_FFMPEG) {
    $ffmpegDir = $env:TRAMOYA_FFMPEG
} else {
    $found = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($found) {
        $ffmpegDir = Split-Path $found.Source -Parent
    }
}
if (-not $ffmpegDir -or -not (Test-Path (Join-Path $ffmpegDir 'ffmpeg.exe'))) {
    throw 'No encuentro ffmpeg.exe: pasa -Ffmpeg <carpeta>, define TRAMOYA_FFMPEG o ponlo en el PATH.'
}
$ffmpegDist = Join-Path $dist 'ffmpeg'
New-Item -ItemType Directory -Force $ffmpegDist | Out-Null
Copy-Item (Join-Path $ffmpegDir 'ffmpeg.exe') $ffmpegDist
if (Test-Path (Join-Path $ffmpegDir 'ffprobe.exe')) {
    Copy-Item (Join-Path $ffmpegDir 'ffprobe.exe') $ffmpegDist
} else {
    Write-Warning "No hay ffprobe.exe junto a $ffmpegDir\ffmpeg.exe; el kit va sin el."
}
Write-Host "   ffmpeg copiado desde $ffmpegDir"

# --- Playwright driver (.playwright) ----------------------------------------------
# Microsoft.Playwright needs its `.playwright` folder (node + driver) loose next to
# the exe. The single-file publish leaves `package\` there but the bundler takes
# `node\win32_x64\node.exe` for a native library and folds it into Tramoya.exe
# (IncludeNativeLibrariesForSelfExtract), where Playwright cannot see it. The
# build outputs made by this same publish still have the complete folder.
$stageBin = Join-Path $windows 'Tramoya.Stage\bin\Release\net9.0'
$appBin = Join-Path $windows 'Tramoya.App\bin\Release\net9.0-windows\win-x64'
$driverDist = Join-Path $dist '.playwright'
$nodeRel = '.playwright\node\win32_x64\node.exe'
if (-not (Test-Path $driverDist)) {
    $fuente = @($appBin, $stageBin) | Where-Object { Test-Path (Join-Path $_ '.playwright') } | Select-Object -First 1
    if (-not $fuente) {
        throw "El publish no ha dejado .playwright en $dist y tampoco esta en $appBin ni en $stageBin."
    }
    Write-Warning ".playwright NO ha salido del publish; se copia entera desde $fuente."
    Copy-Item (Join-Path $fuente '.playwright') $driverDist -Recurse
} elseif (Test-Path (Join-Path $dist $nodeRel)) {
    Write-Host '   .playwright: ha salido del publish completa, con node.exe.'
} else {
    $fuente = @($appBin, $stageBin) | Where-Object { Test-Path (Join-Path $_ $nodeRel) } | Select-Object -First 1
    if (-not $fuente) {
        throw "Falta $nodeRel en el publish y no lo encuentro en $appBin ni en $stageBin."
    }
    Write-Host "   .playwright: ha salido del publish sin node.exe; se copia node\ desde $fuente."
    # Contents, not the folder: Copy-Item nests a folder inside one that already exists.
    Copy-Item (Join-Path $fuente '.playwright\node\*') (Join-Path $driverDist 'node') -Recurse -Force
}
if (-not (Test-Path (Join-Path $dist $nodeRel))) {
    throw "Sigue faltando $nodeRel en $dist."
}

# What single-file publish leaves behind that the kit does not need: the pdb of
# the referenced projects, the WebView2 doc files and a playwright.ps1 that cannot
# work without a loose Microsoft.Playwright.dll.
Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.pdb', '.xml' -or $_.Name -eq 'playwright.ps1' } | Remove-Item -Force

# --- Chromium -------------------------------------------------------------------
$navegador = Join-Path $dist 'navegador'
if ($SkipBrowser) {
    Write-Host '== Chromium: omitido (-SkipBrowser) =='
} elseif ($Navegador) {
    Write-Host "== Chromium: copiado de $Navegador =="
    New-Item -ItemType Directory -Force $navegador | Out-Null
    foreach ($prefijo in @('chromium-', 'chromium_headless_shell-', 'winldd-', 'ffmpeg-')) {
        $ultimo = Get-ChildItem $Navegador -Directory | Where-Object { $_.Name -like "$prefijo*" -and (Test-Path (Join-Path $_.FullName 'INSTALLATION_COMPLETE')) } |
            Sort-Object { [int]($_.Name.Substring($prefijo.Length)) } | Select-Object -Last 1
        if (-not $ultimo) { throw "No hay ninguna carpeta $prefijo* completa en $Navegador." }
        Copy-Item $ultimo.FullName (Join-Path $navegador $ultimo.Name) -Recurse -Force
        Write-Host "   $($ultimo.Name)"
    }
} else {
    Write-Host '== Chromium (Playwright install) =='
    # playwright.ps1 loads Microsoft.Playwright.dll from its own folder and calls
    # Program.Main, so it needs the loose dll next to it. In a single-file publish
    # the dll is inside the exe and the script in dist cannot work; the Stage bin
    # (a library) has no copy-local dlls either. The App build output that this
    # publish just made has the script, the dll and the driver together.
    $playwrightScript = $null
    foreach ($carpeta in @($dist, $appBin, $stageBin)) {
        if ((Test-Path (Join-Path $carpeta 'playwright.ps1')) -and (Test-Path (Join-Path $carpeta 'Microsoft.Playwright.dll'))) {
            $playwrightScript = Join-Path $carpeta 'playwright.ps1'
            break
        }
    }
    if (-not $playwrightScript) {
        throw "No encuentro playwright.ps1 con Microsoft.Playwright.dll al lado ni en $dist, ni en $appBin, ni en $stageBin."
    }
    Write-Host "   usando $playwrightScript"
    $previo = $env:PLAYWRIGHT_BROWSERS_PATH
    try {
        $env:PLAYWRIGHT_BROWSERS_PATH = $navegador
        New-Item -ItemType Directory -Force $navegador | Out-Null
        & $playwrightScript install chromium
        if ($LASTEXITCODE -ne 0) {
            throw "playwright install chromium ha fallado (codigo $LASTEXITCODE)."
        }
    } finally {
        $env:PLAYWRIGHT_BROWSERS_PATH = $previo
    }
    $chromium = Get-ChildItem $navegador -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue
    if (-not $chromium) {
        throw "Playwright no ha dejado ningun chromium-* en $navegador."
    }
    Write-Host "   Chromium en $($chromium[0].FullName)"
}

# --- LEEME.txt ------------------------------------------------------------------
# Single-quoted here-string: backticks and «» must reach the file as they are.
$leeme = @'
Tramoya VERSION para Windows
============================

Haz doble clic en Tramoya.exe. No instala nada: todo queda en esta carpeta.

- Los vídeos terminados se guardan en la carpeta «Mis vídeos», aquí al lado
  (también se abre desde la propia ventana de Tramoya).
- Si copias la carpeta entera a otro sitio o a un pendrive, sigue funcionando.
- Tramoya trae su propio Chromium en la carpeta «navegador»; no toca el
  navegador que tengas instalado.
- Para desinstalar, borra la carpeta.

Desde una terminal, Tramoya.exe acepta los mismos subcomandos que «tramoya»
en Linux y Mac (Tramoya.exe --help).
'@
$leeme = $leeme.Replace('VERSION', $version)
Set-Content -Path (Join-Path $dist 'LEEME.txt') -Value $leeme -Encoding UTF8

# --- zip ------------------------------------------------------------------------
$zip = Join-Path $distRoot "Tramoya-$version-windows.zip"
Write-Host "== zip: $zip =="
if (Test-Path $zip) {
    Remove-Item $zip -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
# .NET Framework writes backslashes in zip entries unless told otherwise, which
# breaks extraction on anything but Windows Explorer.
[System.AppContext]::SetSwitch('Switch.System.IO.Compression.ZipFile.UseBackslash', $false)
[System.IO.Compression.ZipFile]::CreateFromDirectory($dist, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

# --- report ---------------------------------------------------------------------
Write-Host ''
Write-Host "== Contenido de $dist =="
Get-ChildItem $dist -Force | Sort-Object Name | ForEach-Object {
    if ($_.PSIsContainer) {
        $size = (Get-ChildItem $_.FullName -Recurse -File -Force | Measure-Object Length -Sum).Sum
        '{0,12:N0}  {1}\' -f $size, $_.Name
    } else {
        '{0,12:N0}  {1}' -f $_.Length, $_.Name
    }
}
Write-Host ''
Write-Host ('zip: {0:N0} bytes' -f (Get-Item $zip).Length)
Write-Host 'Hecho.'
