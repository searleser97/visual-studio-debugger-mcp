@echo off
set SCRIPT_DIR=%~dp0
set EXE=%SCRIPT_DIR%bin\Release\net8.0-windows\VisualStudioDebuggerMcp.exe

echo [visual-studio-debugger] Building... 1>&2
dotnet build "%SCRIPT_DIR%VisualStudioDebuggerMcp.csproj" -c Release --nologo -v q 1>&2
if errorlevel 1 exit /b 1

"%EXE%" %*
