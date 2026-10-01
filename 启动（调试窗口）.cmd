@echo off
rem ===========================================================================
rem  WorkBuddy PointPal - visible launcher, kept for troubleshooting.
rem  A console window stays open so errors and log lines are visible.
rem  For daily use prefer the .vbs launcher (silent).
rem ===========================================================================
setlocal
cd /d "%~dp0"

if not exist "sprite.png" (
  echo [ERROR] sprite.png not found.
  echo         Build it once from your artwork:
  echo             powershell -ExecutionPolicy Bypass -File make_sprite.ps1 -Source "path\to\image.png"
  pause
  exit /b 1
)

echo Starting WorkBuddy PointPal ...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0wb_pet.ps1" %*
echo.
echo Pet exited.
pause
