@echo off
rem PC Crash Analyzer - final UI (WPF) launcher.
rem Builds only when source files changed since the last build, then starts the UI.
rem 2026-11-18: also builds the collector (BlackBox) and the demo fault program (FaultLab) when their code changed,
rem so that the UI's [Start collector] and [Demo: cause a problem] use the current code.
rem Build the engine first (engine\README.md). .NET 8 SDK is required.
rem Example: run_ui.bat --open engine\data\samples\s1_anticheat_crash.json
cd /d "%~dp0..\.."
set "FILES=*.cs *.xaml *.csproj *.png"

echo Checking for code changes...
call :build_if_changed "collector\BlackBox" "collector\BlackBox\bin\Release\net8.0-windows\BlackBox.exe" "collector" optional
call :build_if_changed "collector\experiments\FaultLab" "collector\experiments\FaultLab\bin\Release\net8.0-windows\FaultLab.exe" "demo fault program" optional
call :build_if_changed "ui\wpf\CrashAnalyzer.UI" "ui\wpf\CrashAnalyzer.UI\bin\Release\net8.0-windows\CrashAnalyzerUI.exe" "UI" required
if errorlevel 1 exit /b 1

echo Starting the UI...
start "" "ui\wpf\CrashAnalyzer.UI\bin\Release\net8.0-windows\CrashAnalyzerUI.exe" %*
exit

rem ---------------------------------------------------------------------------
rem %1 project folder, %2 built exe, %3 name for messages, %4 required|optional
rem A copy of the sources at the last successful build is kept in <project>\obj\run_ui_last_build.
rem robocopy /L only compares (copies nothing); exit code 0 = nothing differs.
:build_if_changed
set "PDIR=%~1"
set "PEXE=%~2"
set "MIRROR=%PDIR%\obj\run_ui_last_build"
set "NEED=1"
if exist "%PEXE%" if exist "%MIRROR%" (
  robocopy "%PDIR%" "%MIRROR%" %FILES% /S /L /XD bin obj /NJH /NJS /NFL /NDL /NP >nul
  if not errorlevel 1 set "NEED=0"
)
if "%NEED%"=="0" exit /b 0
echo Code changed - building the %~3...
dotnet build "%PDIR%" -c Release -v q -nologo
if errorlevel 1 (
  if /i "%~4"=="required" (
    echo Build failed. Check the errors above.
    pause
    exit /b 1
  )
  echo Could not build the %~3. The UI still starts, but that part may not work. Check the errors above.
  exit /b 0
)
robocopy "%PDIR%" "%MIRROR%" %FILES% /S /PURGE /XD bin obj /NJH /NJS /NFL /NDL /NP >nul
exit /b 0
