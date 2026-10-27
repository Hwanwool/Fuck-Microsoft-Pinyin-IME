$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ImeKeyGuard'

Get-Process -Name ImeKeyGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ImeKeyGuard' -ErrorAction SilentlyContinue
Write-Host 'Autostart entry removed.'

if (-not (Test-Path $dest)) {
    Write-Host 'Not installed.'
    return
}

Add-Type -AssemblyName Microsoft.VisualBasic
try {
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($dest, 'OnlyErrorDialogs', 'SendToRecycleBin')
    Write-Host ('Removed to recycle bin: ' + $dest)
    return
} catch {
    Write-Host ('Folder is in use, scheduling delayed removal: ' + $_.Exception.Message)
}

$tmp = Join-Path $env:TEMP 'ImeKeyGuard_delayed.ps1'
$lines = @(
    'Add-Type -AssemblyName Microsoft.VisualBasic',
    'for ($i = 0; $i -lt 90; $i++) {',
    '    try {',
    "        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory('$dest', 'OnlyErrorDialogs', 'SendToRecycleBin')",
    '        break',
    '    } catch {',
    '        Start-Sleep -Seconds 1',
    '    }',
    '}'
)
Set-Content -Path $tmp -Value $lines -Encoding ASCII
Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tmp)
Write-Host 'The folder will be removed shortly after this window closes (may take up to a minute).'
