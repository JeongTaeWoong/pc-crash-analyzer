@echo off
rem PC Crash Analyzer - start the app with ordinary rights.
cd /d "%~dp0"
if not exist "app\CrashAnalyzerUI.exe" (
  echo app\CrashAnalyzerUI.exe is missing. Unpack the whole folder again.
  pause
  exit /b 1
)
start "" "app\CrashAnalyzerUI.exe" %*
