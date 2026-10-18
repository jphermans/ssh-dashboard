@echo off
rem Build SSH Dashboard.exe with the product icon and bundled assets.
setlocal
cd /d "%~dp0.."

where py >nul 2>nul
if %errorlevel%==0 (
    py -3 -m pip install --upgrade pyinstaller || goto :error
    py -3 -m PyInstaller --noconfirm --clean packaging\SSHDashboard.spec || goto :error
) else (
    python -m pip install --upgrade pyinstaller || goto :error
    python -m PyInstaller --noconfirm --clean packaging\SSHDashboard.spec || goto :error
)

echo.
echo Built: dist\SSH Dashboard\SSH Dashboard.exe
echo Icon:  sshdash\ui\assets\SSHDashboard.ico
goto :eof

:error
echo Build failed.
exit /b 1
