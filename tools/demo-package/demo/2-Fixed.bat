@echo off
rem The same job with a Godot build that has the fix. It must end normally (exit code 0) and the collector must
rem record no crash. The project is restored from project-original first.
cd /d "%~dp0"
robocopy "project-original" "game-project" /MIR /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 (
  echo Could not prepare the project folder.
  pause
  exit /b 1
)
echo Opening the same project with {FIXED_EXE} ...
"godot-fixed\{FIXED_EXE}" --headless --disable-crash-handler --path "%~dp0game-project" --import
echo.
echo Godot ended with exit code %errorlevel%. (0 = the import finished.)
pause
