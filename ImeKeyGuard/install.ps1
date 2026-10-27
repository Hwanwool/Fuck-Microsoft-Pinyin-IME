$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ImeKeyGuard'

if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Force -Path $dest | Out-Null }

Get-Process -Name ImeKeyGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$same = ($src.TrimEnd('\') -ieq $dest.TrimEnd('\'))
if (-not $same) {
    foreach ($f in @('ImeKeyGuard.exe', 'ImeKeyGuard.cs', 'install.ps1', 'uninstall.ps1', 'install.bat', 'uninstall.bat', 'readme.md')) {
        $p = Join-Path $src $f
        if (Test-Path $p) { Copy-Item -Force $p $dest }
    }
}

$exe = Join-Path $dest 'ImeKeyGuard.exe'
if (-not (Test-Path $exe)) { throw ('ImeKeyGuard.exe not found in ' + $dest) }

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $run -Name 'ImeKeyGuard' -Value ('"' + $exe + '"')

Start-Process $exe
Start-Sleep -Seconds 1

Write-Host ('Installed: ' + $exe)
Write-Host ('Autostart: ' + (Get-ItemProperty -Path $run -Name 'ImeKeyGuard').ImeKeyGuard)
Write-Host ('Running: ' + ((Get-Process -Name ImeKeyGuard -ErrorAction SilentlyContinue | Measure-Object).Count))
