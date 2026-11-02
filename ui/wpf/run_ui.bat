@echo off
rem PC Crash Analyzer - final UI (WPF) launcher.
rem Builds only when UI source files changed since the last build, then starts the UI.
rem Build the engine first (engine\README.md). .NET 8 SDK is required.
rem Example: run_ui.bat --open engine\data\samples\s1_anticheat_crash.json
cd /d "%~dp0..\.."
set "UIDIR=ui\wpf\CrashAnalyzer.UI"
set "EXE=%UIDIR%\bin\Release\net8.0-windows\CrashAnalyzerUI.exe"
rem Copy of the sources at the last successful build. robocopy /L only compares (copies nothing).
set "MIRROR=%UIDIR%\obj\run_ui_last_build"
set "FILES=*.cs *.xaml *.csproj *.png"

echo Checking for code changes...
set "NEED=1"
if exist "%EXE%" if exist "%MIRROR%" (
  robocopy "%UIDIR%" "%MIRROR%" %FILES% /S /L /XD bin obj /NJH /NJS /NFL /NDL /NP >nul
  rem robocopy exit code 0 = nothing differs
  if not errorlevel 1 set "NEED=0"
)

if "%NEED%"=="1" (
  echo Code changed - building the UI...
  dotnet build "%UIDIR%" -c Release -v q -nologo
  if errorlevel 1 (
    echo Build failed. Check the errors above.
    pause
    exit /b 1
  )
  robocopy "%UIDIR%" "%MIRROR%" %FILES% /S /PURGE /XD bin obj /NJH /NJS /NFL /NDL /NP >nul
)

echo Starting the UI...
start "" "%EXE%" %*
exit
