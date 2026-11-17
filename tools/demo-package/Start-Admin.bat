@echo off
rem PC Crash Analyzer - start the app as administrator. Windows asks for consent in its own window (UAC);
rem nothing is bypassed. As administrator the collector also records process starts, exits and module loads.
cd /d "%~dp0"
if not exist "app\CrashAnalyzerUI.exe" (
  echo app\CrashAnalyzerUI.exe is missing. Unpack the whole folder again.
  pause
  exit /b 1
)
powershell -NoProfile -Command "Start-Process -FilePath '%~dp0app\CrashAnalyzerUI.exe' -WorkingDirectory '%~dp0app' -Verb RunAs"
if errorlevel 1 (
  echo The app was not started as administrator. Use Start.bat for ordinary rights.
  pause
)
