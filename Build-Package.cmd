@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0packaging"

echo ============================================================
echo RVC Studio NVIDIA - one-click release packaging
echo ============================================================
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0packaging\Build-Release.ps1" %*
set "RVC_BUILD_EXIT=%ERRORLEVEL%"

echo.
if not "%RVC_BUILD_EXIT%"=="0" (
    echo Packaging failed with exit code %RVC_BUILD_EXIT%.
    echo Review the error above, then run this file again.
) else (
    echo Packaging completed successfully.
    echo Output: %~dp0packaging\output\installer
)
echo.
pause
exit /b %RVC_BUILD_EXIT%
