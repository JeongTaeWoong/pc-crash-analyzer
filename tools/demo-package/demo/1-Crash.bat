@echo off
rem Reproduces a documented bug of Godot 4.3: importing an SVG that embeds a JPEG ends the program
rem (github.com/godotengine/godot issue 98341, fixed in 4.5). The same command as lab scenario real-godot-svg.
rem Start the app and its collector first. Nothing is installed; the project is restored from project-original
rem every time, so this can be run again and again.
cd /d "%~dp0"
robocopy "project-original" "game-project" /MIR /NJH /NJS /NFL /NDL /NP >nul
if errorlevel 8 (
  echo Could not prepare the project folder.
  pause
  exit /b 1
)
echo Opening the project with Godot 4.3 ...
"godot\Godot_v4.3-stable_win64.exe" --headless --disable-crash-handler --path "%~dp0game-project" --import
echo.
echo Godot ended with exit code %errorlevel%. (0 would mean the import finished: the bug did not show.)
pause
