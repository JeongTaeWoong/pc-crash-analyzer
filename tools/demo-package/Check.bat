@echo off
rem PC Crash Analyzer - checks that this folder works on this PC. Nothing is installed or changed.
rem   1. the app starts and gets a diagnosis of an example record from the engine
rem   2. the collector starts (it prints a summary of the Windows event log it can read, then ends)
chcp 65001 >nul
cd /d "%~dp0"
set "OUT=%TEMP%\pca_check_%RANDOM%.txt"

echo [1/2] app + engine ...
"app\CrashAnalyzerUI.exe" --selftest "%OUT%"
if errorlevel 1 (
  echo FAIL  app + engine
) else (
  echo OK    app + engine
)
if exist "%OUT%" (
  type "%OUT%"
  echo.
  del "%OUT%"
) else (
  echo        the app wrote no result: it did not start at all
)

echo [2/2] collector ...
"collector\BlackBox\bin\Release\net8.0-windows\BlackBox.exe" --verify-eventlog 1 >"%OUT%" 2>&1
if errorlevel 1 (
  echo FAIL  collector
  type "%OUT%"
) else (
  echo OK    collector
)
if exist "%OUT%" del "%OUT%"

echo.
pause
