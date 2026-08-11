@echo off
set SCRIPT_DIR=%~dp0
set EXE=%SCRIPT_DIR%bin\Release\net8.0-windows\VisualStudioDebuggerMcp.exe

if not exist "%EXE%" (
  echo [visual-studio-debugger] Missing executable: %EXE% 1>&2
  echo [visual-studio-debugger] Build the project in Release configuration before starting the MCP server. 1>&2
  exit /b 1
)

"%EXE%" %*
