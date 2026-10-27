@echo off
setlocal
set QUIET=0
if /i "%~1"=="/quiet" set QUIET=1
cd /d "%~dp0"
echo Installing ImeKeyGuard...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
set RC=%ERRORLEVEL%
echo.
if not "%RC%"=="0" (
    echo Install FAILED with code %RC%.
) else (
    echo Install finished.
)
if "%QUIET%"=="0" pause
endlocal
exit /b %RC%
