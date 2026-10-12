@echo off
rem PC Crash Analyzer UI launcher (no console window). Build the engine first (engine\README.md).
where pythonw >nul 2>&1
if errorlevel 1 (
  echo Python is not installed. Install it from https://www.python.org and try again.
  pause
  exit /b 1
)
start "" pythonw "%~dp0crash_ui.py" %*
