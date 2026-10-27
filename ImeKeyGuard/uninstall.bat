@echo off
setlocal
set QUIET=0
if /i "%~1"=="/quiet" set QUIET=1
cd /d "%~dp0"
echo Uninstalling ImeKeyGuard...
echo.
copy /y "%~dp0uninstall.ps1" "%TEMP%\ImeKeyGuard_uninstall.ps1" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\ImeKeyGuard_uninstall.ps1"
set RC=%ERRORLEVEL%
del "%TEMP%\ImeKeyGuard_uninstall.ps1" >nul 2>&1
echo.
if not "%RC%"=="0" (
    echo Uninstall FAILED with code %RC%.
) else (
    echo Uninstall finished.
)
if "%QUIET%"=="0" pause
endlocal
exit /b %RC%
