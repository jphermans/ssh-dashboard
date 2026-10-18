# Installs Python (with Tk) for the current user on Windows.
# Called by SSH Dashboard.exe when no suitable interpreter is found.
# Uses winget (the Windows package manager) and verifies Tk before reporting
# success. NuGet's "python" package is intentionally not used: it ships without
# tcl/tk, so tkinter cannot start.

$ErrorActionPreference = "Stop"
$root = Join-Path $env:LOCALAPPDATA "SSHDashboard"
New-Item -ItemType Directory -Force -Path $root | Out-Null
$log = Join-Path $root "install.log"
$pathFile = Join-Path $root "python-path.txt"

function Log([string]$message) {
    $line = "{0}  {1}" -f (Get-Date -Format s), $message
    Add-Content -Path $log -Value $line
    Write-Host $line
}

function Test-Tk([string]$exe) {
    try {
        $output = & $exe -c "import sys, tkinter; print(sys.version.split()[0] + '|' + str(tkinter.TkVersion))" 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            return ($output | Select-Object -First 1)
        }
    } catch { }
    return $null
}

function Save-Python([string]$exe) {
    if (-not (Test-Path $exe)) { return }
    $probe = Test-Tk $exe
    if (-not $probe) { return }
    $windowed = $exe -replace '(?i)python\.exe$', 'pythonw.exe'
    if (-not (Test-Path $windowed)) { $windowed = $exe }
    Set-Content -Path $pathFile -Value $windowed -Encoding ASCII
    Log "Using $windowed ($probe)"
    exit 0
}

function Get-KnownPython {
    $candidates = @()
    foreach ($version in @("314", "313", "312")) {
        $candidates += Join-Path $env:LOCALAPPDATA "Programs\Python\Python$version\python.exe"
        if ($env:ProgramFiles) {
            $candidates += Join-Path $env:ProgramFiles "Python$version\python.exe"
        }
    }
    return $candidates
}

Log "Looking for an existing Python installation"
foreach ($candidate in Get-KnownPython) { Save-Python $candidate }
$onPath = Get-Command python.exe -ErrorAction SilentlyContinue
if ($onPath) { Save-Python $onPath.Source }

$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if (-not $winget) { $winget = Get-Command winget -ErrorAction SilentlyContinue }

if (-not $winget) {
    Log "winget is not available on this PC"
    exit 1
}

Log "Installing Python 3.13 with winget (current user)"
try {
    & $winget.Source install --id Python.Python.3.13 --exact --silent `
        --accept-package-agreements --accept-source-agreements `
        --disable-interactivity 2>&1 | ForEach-Object { Log "$_" }
} catch {
    Log "winget failed: $_"
}

Log "Checking the installation"
foreach ($candidate in Get-KnownPython) { Save-Python $candidate }
$onPath = Get-Command python.exe -ErrorAction SilentlyContinue
if ($onPath) { Save-Python $onPath.Source }

Log "Python was not installed successfully"
exit 1
