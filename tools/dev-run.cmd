@echo off
rem Starts a local dedicated server (console window) and N client windows (default 2).
rem Usage: tools\dev-run.cmd [clientCount]
setlocal
set "COUNT=%~1"
if "%COUNT%"=="" set "COUNT=2"

set "GODOT_DIR=E:\Godot Install\Godot_v4.7.2-stable_mono_win64"
set "GODOT=%GODOT_DIR%\Godot_v4.7.2-stable_mono_win64.exe"
set "GODOT_CONSOLE=%GODOT_DIR%\Godot_v4.7.2-stable_mono_win64_console.exe"
set "PROJECT=%~dp0.."

rem Keep everything off C: (see E:\claude\CLAUDE.md).
set "DOTNET_ROOT=E:\claude\tools\dotnet"
set "PATH=E:\claude\tools\dotnet;%PATH%"
set "TEMP=E:\claude\_tmp"
set "TMP=E:\claude\_tmp"
set "APPDATA=E:\claude\_appdata"

rem Build C# first so every process loads the latest code.
dotnet build "%PROJECT%\ExploreTogether.csproj" -nologo -v q || exit /b 1

start "ExploreTogether Server" "%GODOT_CONSOLE%" --headless --path "%PROJECT%" -- --server

for /L %%i in (1,1,%COUNT%) do (
  call :client %%i
)
exit /b 0

:client
set /a POSX=(%1-1)*970
start "" "%GODOT%" --path "%PROJECT%" --resolution 960x540 --position %POSX%,60 -- --connect 127.0.0.1 --name Player%1
exit /b 0
