@echo off
rem Build Expzip. Requires the .NET 10 SDK.
rem
rem   build.cmd          build the redistributable publish\win-x64\Expzip.exe
rem   build.cmd -Test    build, then run the UI tests in tests\ui
rem   build.cmd -Debug   build for development only (no single-file publish)
rem
rem This file stays ASCII on purpose: cmd.exe reads .cmd in the ANSI code page,
rem and some Japanese characters end in byte 0x5C, which it treats as an escape.
rem The Japanese messages live in build.ps1.

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
