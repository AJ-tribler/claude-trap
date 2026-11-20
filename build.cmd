@echo off
rem Builds claude-trap.exe from claude_trap.cs with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo C# compiler not found: %CSC%
  exit /b 1
)
"%CSC%" /nologo /O+ /platform:x64 /out:"%~dp0claude-trap.exe" "%~dp0claude_trap.cs" || exit /b 1
echo Built %~dp0claude-trap.exe
