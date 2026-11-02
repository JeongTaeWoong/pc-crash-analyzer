@echo off
rem PC Crash Analyzer - final UI (WPF) launcher. Builds once if needed. Build the engine first (engine\README.md).
rem Example: run_ui.bat --open engine\data\samples\s1_anticheat_crash.json
cd /d "%~dp0..\.."
if not exist "ui\wpf\CrashAnalyzer.UI\bin\Release\net8.0-windows\CrashAnalyzerUI.exe" (
  echo First run: building the UI...
  dotnet build ui\wpf\CrashAnalyzer.UI -c Release
  if errorlevel 1 (
    echo Build failed. Check the errors above. .NET 8 SDK is required.
    pause
    exit /b 1
  )
)
start "" "ui\wpf\CrashAnalyzer.UI\bin\Release\net8.0-windows\CrashAnalyzerUI.exe" %*
