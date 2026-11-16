@echo off
rem Engine smoke test: server + headless clients through scripted scenarios; exit code 0 = pass.
rem Usage: tools\smoke-test.cmd [-Only coop,combat,death] [-NoBuild]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0smoke-test.ps1" %*
exit /b %ERRORLEVEL%
