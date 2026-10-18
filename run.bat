@echo off
rem Launch SSH Dashboard on Windows with the best available Python.
setlocal
cd /d "%~dp0"

where py >nul 2>nul
if errorlevel 1 goto :plain
py -3.14 ssh_dashboard.py %*
if not errorlevel 1 goto :done
py -3.13 ssh_dashboard.py %*
if not errorlevel 1 goto :done
py -3 ssh_dashboard.py %*
goto :done

:plain
python ssh_dashboard.py %*

:done
if errorlevel 1 pause
