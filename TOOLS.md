# Tool Reference

`VisualStudioTools.cs` defines the protocol schemas exposed by the MCP server. This file provides
generic usage sequences; it is not a second schema source.

## Open and connect

```text
open_visual_studio(
  visualStudioExecutable="C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe",
  solutionPath="C:\repos\sample\Sample.sln",
  solutionPattern="*Sample.sln")
```

For an already-open instance:

```text
list_visual_studio_instances()
connect_to_visual_studio(solutionPattern="*Sample.sln")
```

## Build and launch

```text
build_solution(solutionConfiguration="Debug")
start_debugging(
  startupProjects=["src\Sample.Web\Sample.Web.csproj"],
  solutionConfiguration="Debug",
  launchCommand="Debug.Start",
  disableJustMyCode=false)
```

`start_debugging` only issues the launch command. Observe Visual Studio and application readiness
with separate tools:

```text
wait_for_debug_mode(debugMode="Run")
wait_for_ports(ports=[5000])
```

Both waits are state-driven and have no default deadline.
Each Visual Studio state probe remains bounded. If Visual Studio enters Break while waiting for
Run, `wait_for_debug_mode` returns `blocked=true` so the caller can inspect or continue the stop.

## Breakpoint workflow

```text
set_breakpoint(
  file="src\Sample.Web\Controllers\HomeController.cs",
  line=42,
  condition="request != null")

wait_for_break(
  timeoutSeconds=300,
  autoContinueAllExceptionBreaks=false,
  expectedExceptionPatterns=["System.OperationCanceledException"])

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

wait_for_break(timeoutSeconds=300)
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
