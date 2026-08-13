# Tool Reference

`VisualStudioTools.cs` defines the protocol schemas exposed by the MCP server. This file provides
generic usage sequences; it is not a second schema source.

## Open and connect

```text
open_visual_studio(
  solutionPath="C:\repos\sample\Sample.sln")
```

The solution must be an absolute existing path. The server discovers Visual Studio through
Installer's `vswhere.exe`, including prerelease installations, and then checks standard
`Program Files\Microsoft Visual Studio\<version>\<edition>\Common7\IDE` locations.

Prefer this tool over launching `devenv.exe` directly. It validates the solution, reports Visual
Studio's launcher-process handoff, and returns the process ID and next MCP operation needed to
establish a debugger target.

Expected validation, discovery, and Windows launch failures return `started=false` with an error
code/message, the provided and resolved paths, discovered `devenv.exe` locations, and suggested
next actions. A successful result identifies the executable as `AutoDiscovered`. Visual Studio may
hand off to an existing or replacement process. The tool does not suppress additional instances;
use `list_visual_studio_instances`, `connect_to_visual_studio`, and `close_visual_studio` to manage
them explicitly.

If automatic discovery cannot find the intended installation, locate `devenv.exe` using the
returned guidance, then retry through the explicit fallback rather than launching it directly:

```text
open_visual_studio_with_exe_path(
  solutionPath="C:\repos\sample\Sample.sln",
  visualStudioExecutablePath="D:\Visual Studio\Common7\IDE\devenv.exe")
```

This preserves the same validation, process-handoff guidance, and structured feedback as the
automatic tool. Do not guess an installation path.

Poll `list_visual_studio_instances` until the solution appears, then connect:

```text
list_visual_studio_instances()
connect_to_visual_studio(solutionPattern="*Sample.sln")
```

## Build and launch

```text
start_build(solutionConfiguration="Debug")
get_build_status()
start_debugging(
  startupProjects=["src\Sample.Web\Sample.Web.csproj"],
  solutionConfiguration="Debug",
  launchCommand="Debug.Start",
  disableJustMyCode=false)
```

Poll `get_build_status` with separate calls until it returns `Succeeded` or `Failed`. Visual Studio
may temporarily reject automation calls while busy; those probes return `Running` with
`buildState="VisualStudioBusy"` instead of failing.

`start_debugging` only issues the launch command. Observe Visual Studio and application readiness
with separate short status calls:

```text
get_visual_studio_state()
get_port_status(ports=[5000])
```

Poll from the client until the requested state is reached. This keeps every MCP request short and
avoids transport deadlines.

`get_visual_studio_state` also inspects Visual Studio's native window state before calling EnvDTE.
When a modal dialog blocks automation, it returns `automationAvailable=false`, `isBlocked=true`,
`blockReason="ModalDialog"`, and `windowState.blockingDialogs` with the dialog title, message text,
and available buttons. This lets callers identify and resolve prompts such as unsupported Hot
Reload or exception dialogs without waiting for an EnvDTE timeout.

Expected state failures return `success=false` with an error code/message, the requested target,
EnvDTE-visible instances, native `devenv` processes, and suggested next actions. This distinguishes
a stale process ID, unmatched or ambiguous target, no running Visual Studio, and temporarily
unavailable EnvDTE automation without reducing them to a generic MCP error. If the isolated state
worker itself times out or exits, the host returns the same structured shape with
`VisualStudioStateTimedOut` or `VisualStudioStateWorkerFailed`; in that case
`availableInstancesUnavailableReason` explains why only native process diagnostics are present.

After inspecting the reported choices, callers can resolve a prompt explicitly:

```text
click_visual_studio_dialog_button(
  dialogTitle="Hot Reload",
  buttonName="OK")
```

The tool only invokes an enabled button with the requested visible name; it does not guess or
automatically accept dialogs.

`stop_debugging`, `pause_execution`, and the three stepping tools also return after initiating the
action. Poll `get_visual_studio_state` before issuing the next debugger command.

Inspection, expression evaluation, exception inspection, continue, and stepping require
`debugMode="Break"`. Always call `get_visual_studio_state` first; `breakReason` distinguishes a
source breakpoint from an exception, completed step, manual break, or other debugger stop.

## Breakpoint workflow

```text
set_breakpoint(
  file="src\Sample.Web\Controllers\HomeController.cs",
  line=42,
  condition="request != null")

get_visual_studio_state()

inspect(
  expressions=["request.Path", "response.StatusCode"],
  frameIndex=1,
  autoContinue=false)

continue_execution()
```

## Exception workflow

```text
set_exception_settings(
  exceptionName="System.InvalidOperationException",
  breakWhenThrown=true,
  breakWhenUserUnhandled=true)

get_visual_studio_state()
get_current_exception()
get_stack_trace()
get_variables(frameIndex=1)
```

## Execution control

```text
pause_execution()
step_into()
step_over()
step_out()
continue_execution()
stop_debugging()
close_visual_studio(saveAll=true)
```
