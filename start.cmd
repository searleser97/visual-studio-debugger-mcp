@echo off
set SCRIPT_DIR=%~dp0
set INSTALLED_EXE=%LOCALAPPDATA%\VisualStudioDebuggerMcp\VisualStudioDebuggerMcp.exe
set SOURCE_EXE=%SCRIPT_DIR%bin\Release\net8.0-windows\VisualStudioDebuggerMcp.exe

if exist "%INSTALLED_EXE%" (
  set EXE=%INSTALLED_EXE%
) else (
  set EXE=%SOURCE_EXE%
)

if not exist "%EXE%" (
  echo [visual-studio-debugger] No installed or source-built executable was found. 1>&2
  echo [visual-studio-debugger] Run install.ps1 or build the project in Release configuration. 1>&2
  exit /b 1
)

"%EXE%" %*
