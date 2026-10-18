@echo off
rem Build the small native Windows launcher (no bundled Python) and a portable zip.
rem Requires a Visual Studio Developer Command Prompt (cl.exe, rc.exe on PATH).
setlocal
cd /d "%~dp0.."

where cl >nul 2>nul
if errorlevel 1 (
    echo Please run this from "x64 Native Tools Command Prompt for VS".
    exit /b 1
)
where rc >nul 2>nul
if errorlevel 1 (
    echo rc.exe not found. Please use a Developer Command Prompt.
    exit /b 1
)

rc /nologo /fo launcher\windows\launcher.res launcher\windows\launcher.rc || exit /b 1
cl /nologo /O1 /GS- /MT /DUNICODE /D_UNICODE /W3 ^
   launcher\windows\launcher.c launcher\windows\launcher.res ^
   /Fe:"SSH Dashboard.exe" ^
   /link /SUBSYSTEM:WINDOWS user32.lib shell32.lib advapi32.lib || exit /b 1

if exist dist\windows rmdir /s /q dist\windows
mkdir dist\windows
copy /y "SSH Dashboard.exe" dist\windows\ >nul
copy /y ssh_dashboard.py dist\windows\ >nul
copy /y launcher\windows\install-python.ps1 dist\windows\ >nul
copy /y README.md dist\windows\ >nul
xcopy /e /i /q /y sshdash dist\windows\sshdash >nul
if exist dist\windows\sshdash\__pycache__ rmdir /s /q dist\windows\sshdash\__pycache__

powershell -NoProfile -Command "Compress-Archive -Path 'dist\windows\*' -DestinationPath 'dist\SSH-Dashboard-1.0.3-windows-x64.zip' -Force"

echo.
for %%F in ("SSH Dashboard.exe") do echo Launcher: %%~zF bytes
echo Package:  dist\SSH-Dashboard-1.0.3-windows-x64.zip
echo Note:     Python 3.9+ with Tk 8.6 is required on the target PC.
