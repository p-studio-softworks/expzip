@echo off
rem Rebuild the SFX stub (sfx-x86.exe). See README.md for the reasoning.
rem
rem Building Expzip itself does NOT need this: the result is checked in as
rem src\Expzip\Resources\sfx-x86.exe, so "dotnet build" is enough.
rem Run this only after editing sfx.c.
rem
rem Needs: Visual Studio 2022 with the C++ workload (cl.exe and link.exe).
rem
rem This file stays ASCII on purpose. cmd.exe reads .cmd as the ANSI code page,
rem and some Japanese characters end in byte 0x5C, which it treats as an escape.

setlocal
set "VSROOT=%ProgramFiles%\Microsoft Visual Studio\2022"
set "VCVARS="
for %%e in (Community Professional Enterprise) do (
  if exist "%VSROOT%\%%e\VC\Auxiliary\Build\vcvars32.bat" set "VCVARS=%VSROOT%\%%e\VC\Auxiliary\Build\vcvars32.bat"
)
if not defined VCVARS (
  if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars32.bat" (
    set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars32.bat"
  )
)
if not defined VCVARS (
  echo Visual Studio 2022 C++ build tools were not found.
  exit /b 1
)

rem vcvars32 wants vswhere.exe, which the installer keeps outside PATH.
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VCVARS%" >nul

cd /d "%~dp0"

rem x86 so the joiner runs on both 32-bit and 64-bit Windows.
rem /NODEFAULTLIB keeps it at ~4.6KB; with the CRT it would be over 100KB.
cl /nologo /utf-8 /O1 /GS- /Gs1048576 /W3 sfx.c ^
   /link /NODEFAULTLIB /ENTRY:WinMainCRTStartup /SUBSYSTEM:WINDOWS ^
   /MERGE:.rdata=.text /OUT:sfx-x86.exe kernel32.lib user32.lib
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

del /q join.obj 2>nul
copy /y sfx-x86.exe "..\..\src\Expzip\Resources\sfx-x86.exe" >nul
for %%s in (sfx-x86.exe) do echo Built sfx-x86.exe: %%~zs bytes
endlocal
