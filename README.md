# Visual Studio Debugger MCP

Generic Windows MCP server for controlling and inspecting Visual Studio through EnvDTE.

## Reliability model

The MCP host does not retain a COM proxy. Each EnvDTE command or state probe runs in a fresh STA
worker process with a hard timeout. Long-running waits repeat those bounded probes without imposing
an overall deadline. A hung EnvDTE call is terminated without wedging the MCP server. A named
cross-process mutex prevents concurrent workers from overwhelming Visual Studio.

The global Copilot registration is:

```json
"myvs-debugger": {
  "type": "stdio",
  "command": "C:/path/to/VisualStudioDebuggerMcp/start.cmd",
  "args": []
}
```

Restart Copilot CLI or reconnect the server through `/mcp` after changing the MCP executable.

## Install

For a source checkout, build once before registering `start.cmd`:

```powershell
dotnet build VisualStudioDebuggerMcp.csproj -c Release
```

Tagged releases publish a framework-dependent Windows x64 ZIP. Install the latest release without
building from source:

```powershell
.\install.ps1
```

The installer prints the executable path to use as the MCP `command`. The machine must have the
.NET 8 runtime and Visual Studio installed.

Maintainers create a release by pushing a `v*` tag. The release workflow publishes the complete
runtime directory; generated `bin/` output remains excluded from source control.

## Tool reference

`VisualStudioTools.cs` is the authoritative MCP schema. See `TOOLS.md` for a human-readable
function list and generic invocation examples.

Project-specific paths, startup projects, launch policies, and expected exceptions intentionally
do not live in this repository. Supply them through tool parameters or a private skill.
