using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace VisualStudioDebuggerMcp;

[McpServerToolType]
internal sealed class VisualStudioTools
{
    [McpServerTool(Name = "list_visual_studio_instances", ReadOnly = true)]
    [Description("Lists running Visual Studio instances, process IDs, and loaded solutions.")]
    public static Task<string> ListInstances(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("list_instances", includeTarget: false, cancellationToken: ct);

    [McpServerTool(Name = "connect_to_visual_studio", ReadOnly = true)]
    [Description(
        "Selects a running Visual Studio instance by solution wildcard or process ID. " +
        "The selected target is reused by later calls.")]
    public static async Task<string> Connect(
        WorkerClient client,
        [Description("Optional solution-path wildcard, such as *MySolution.slnx.")] string? solutionPattern = null,
        [Description("Optional devenv process ID.")] int? visualStudioProcessId = null,
        CancellationToken ct = default)
    {
        client.SetTarget(solutionPattern, visualStudioProcessId);
        return await client.InvokeAsync("get_state", cancellationToken: ct);
    }

    [McpServerTool(Name = "open_visual_studio")]
    [Description(
        "Starts Visual Studio and opens a solution. " +
        "Waits until the instance is available through automation.")]
    public static async Task<string> Open(
        WorkerClient client,
        [Description("Absolute path to devenv.exe.")] string visualStudioExecutable,
        [Description("Absolute path to a .sln or .slnx file.")] string solutionPath,
        [Description("Optional wildcard used to identify the opened solution. Defaults to the solution filename.")] string? solutionPattern = null,
        [Description("Disable Visual Studio Just My Code after opening.")] bool disableJustMyCode = false,
        [Description("Maximum seconds to wait for Visual Studio and the solution to initialize.")] int timeoutSeconds = 180,
        CancellationToken ct = default)
    {
        var effectivePattern = solutionPattern ?? $"*{Path.GetFileName(solutionPath)}";
        client.SetTarget(effectivePattern, null);
        return await client.InvokeAsync(
            "open_visual_studio",
            new JsonObject
            {
                ["visualStudioExecutable"] = visualStudioExecutable,
                ["solutionPath"] = solutionPath,
                ["solutionPattern"] = effectivePattern,
                ["disableJustMyCode"] = disableJustMyCode,
                ["timeoutSeconds"] = timeoutSeconds
            },
            timeoutSeconds + 30,
            cancellationToken: ct);
    }

    [McpServerTool(Name = "close_visual_studio")]
    [Description("Stops debugging and closes the selected Visual Studio instance.")]
    public static Task<string> Close(
        WorkerClient client,
        [Description("Save all modified files before closing.")] bool saveAll = true,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "close_visual_studio",
            new JsonObject { ["saveAll"] = saveAll },
            timeoutSeconds: 120,
            cancellationToken: ct);

    [McpServerTool(Name = "get_visual_studio_state", ReadOnly = true)]
    [Description("Returns solution, build state, debugger mode, break reason, and status-bar text.")]
    public static Task<string> GetState(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("get_state", cancellationToken: ct);

    [McpServerTool(Name = "build_solution")]
    [Description("Builds the selected solution through Visual Studio and waits for completion.")]
    public static Task<string> Build(
        WorkerClient client,
        [Description("Optional Visual Studio solution configuration name, such as Debug.")] string? solutionConfiguration = null,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "build_solution",
            new JsonObject { ["solutionConfiguration"] = solutionConfiguration },
            timeoutSeconds: 900,
            cancellationToken: ct);

    [McpServerTool(Name = "apply_debugger_settings")]
    [Description("Applies generic Visual Studio debugger settings to the selected instance.")]
    public static Task<string> ApplyDebuggerSettings(
        WorkerClient client,
        [Description("Disable Visual Studio Just My Code.")] bool disableJustMyCode,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "apply_debugger_settings",
            new JsonObject { ["disableJustMyCode"] = disableJustMyCode },
            cancellationToken: ct);

    [McpServerTool(Name = "start_debugging")]
    [Description(
        "Optionally applies startup projects, solution configuration, debugger settings, and a launch " +
        "command, then starts debugging through Visual Studio.")]
    public static Task<string> StartDebugging(
        WorkerClient client,
        [Description("Startup project identifiers accepted by Visual Studio.")] string[]? startupProjects = null,
        [Description("Optional solution configuration name, such as Debug.")] string? solutionConfiguration = null,
        [Description("Visual Studio command used to launch debugging.")] string launchCommand = "Debug.Start",
        [Description("Disable Visual Studio Just My Code before launching.")] bool disableJustMyCode = false,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "start_debugging",
            new JsonObject
            {
                ["startupProjects"] = new JsonArray(
                    (startupProjects ?? [])
                    .Select(value => (JsonNode?)JsonValue.Create(value))
                    .ToArray()),
                ["solutionConfiguration"] = solutionConfiguration,
                ["launchCommand"] = launchCommand,
                ["disableJustMyCode"] = disableJustMyCode
            },
            timeoutSeconds: 60,
            cancellationToken: ct);

    [McpServerTool(Name = "wait_for_debug_mode", ReadOnly = true)]
    [Description(
        "Waits for Visual Studio to reach a debugger mode. Each state probe is isolated and " +
        "bounded, while the overall wait has no deadline by default. Waiting for Run returns a " +
        "blocked result if Visual Studio enters Break and requires debugger action.")]
    public static Task<string> WaitForDebugMode(
        WorkerClient client,
        [Description("Expected debugger mode: Run, Break, or Design.")] string debugMode = "Run",
        [Description("Optional maximum seconds to wait. Use 0 (default) for no deadline.")] int timeoutSeconds = 0,
        [Description("Polling interval in milliseconds.")] int pollMilliseconds = 1000,
        CancellationToken ct = default) =>
        client.WaitForDebugModeAsync(debugMode, timeoutSeconds, pollMilliseconds, ct);

    [McpServerTool(Name = "get_port_status", ReadOnly = true)]
    [Description("Checks whether one or more TCP ports are currently ready.")]
    public static Task<string> GetPortStatus(
        WorkerClient client,
        [Description("TCP ports to inspect.")] int[] ports,
        [Description("Host name or IP address.")] string host = "127.0.0.1",
        [Description("Per-port connection timeout for non-local hosts.")] int connectTimeoutMilliseconds = 500,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "get_port_status",
            new JsonObject
            {
                ["host"] = host,
                ["ports"] = new JsonArray(
                    ports.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["connectTimeoutMilliseconds"] = connectTimeoutMilliseconds
            },
            includeTarget: false,
            cancellationToken: ct);

    [McpServerTool(Name = "wait_for_ports", ReadOnly = true)]
    [Description(
        "Waits until all or any requested TCP ports are ready. The default wait is state-driven " +
        "with no deadline.")]
    public static Task<string> WaitForPorts(
        WorkerClient client,
        [Description("TCP ports to wait for.")] int[] ports,
        [Description("Host name or IP address.")] string host = "127.0.0.1",
        [Description("Require every port; when false, return after any port is ready.")] bool requireAll = true,
        [Description("Optional maximum seconds to wait. Use 0 (default) for no deadline.")] int timeoutSeconds = 0,
        [Description("Polling interval in milliseconds.")] int pollMilliseconds = 1000,
        [Description("Per-port connection timeout for non-local hosts.")] int connectTimeoutMilliseconds = 500,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "wait_for_ports",
            new JsonObject
            {
                ["host"] = host,
                ["ports"] = new JsonArray(
                    ports.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["requireAll"] = requireAll,
                ["timeoutSeconds"] = timeoutSeconds,
                ["pollMilliseconds"] = pollMilliseconds,
                ["connectTimeoutMilliseconds"] = connectTimeoutMilliseconds
            },
            timeoutSeconds > 0 ? timeoutSeconds + 15 : 0,
            includeTarget: false,
            cancellationToken: ct);

    [McpServerTool(Name = "stop_debugging")]
    [Description("Stops the active Visual Studio debugging session and waits for Design mode.")]
    public static Task<string> StopDebugging(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("stop_debugging", timeoutSeconds: 120, cancellationToken: ct);

    [McpServerTool(Name = "set_breakpoint")]
    [Description(
        "Adds or replaces a source breakpoint. Supports conditional C# expressions. " +
        "Paths may be absolute or relative to the solution directory.")]
    public static Task<string> SetBreakpoint(
        WorkerClient client,
        [Description("Source file path.")] string file,
        [Description("1-based source line.")] int line,
        [Description("Optional C# condition evaluated when the breakpoint is reached.")] string? condition = null,
        [Description("1-based source column.")] int column = 1,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "set_breakpoint",
            new JsonObject
            {
                ["file"] = file,
                ["line"] = line,
                ["condition"] = condition,
                ["column"] = column
            },
            cancellationToken: ct);

    [McpServerTool(Name = "remove_breakpoint")]
    [Description("Removes a breakpoint at a source file and line.")]
    public static Task<string> RemoveBreakpoint(
        WorkerClient client,
        string file,
        int line,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "remove_breakpoint",
            new JsonObject { ["file"] = file, ["line"] = line },
            cancellationToken: ct);

    [McpServerTool(Name = "list_breakpoints", ReadOnly = true)]
    [Description("Lists breakpoints in the selected Visual Studio instance.")]
    public static Task<string> ListBreakpoints(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("list_breakpoints", cancellationToken: ct);

    [McpServerTool(Name = "wait_for_break", ReadOnly = true)]
    [Description(
        "Waits until Visual Studio pauses. Profile-configured expected exception breaks may be " +
        "automatically continued; real breakpoint, step, and user-break stops are returned.")]
    public static Task<string> WaitForBreak(
        WorkerClient client,
        [Description("Optional maximum seconds to wait. Use 0 (default) for no deadline.")] int timeoutSeconds = 0,
        [Description("Polling interval in milliseconds.")] int pollMilliseconds = 500,
        [Description("Automatically continue every exception-triggered stop.")] bool autoContinueAllExceptionBreaks = false,
        [Description("Exception type wildcard patterns to auto-continue, such as System.Net.Http.*.")] string[]? expectedExceptionPatterns = null,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "wait_for_break",
            new JsonObject
            {
                ["timeoutSeconds"] = timeoutSeconds,
                ["pollMilliseconds"] = pollMilliseconds,
                ["autoContinueAllExceptionBreaks"] = autoContinueAllExceptionBreaks,
                ["expectedExceptionPatterns"] = new JsonArray(
                    (expectedExceptionPatterns ?? [])
                    .Select(value => (JsonNode?)JsonValue.Create(value))
                    .ToArray())
            },
            timeoutSeconds > 0 ? timeoutSeconds + 15 : 0,
            cancellationToken: ct);

    [McpServerTool(Name = "get_stack_trace", ReadOnly = true)]
    [Description("Returns the current thread's stack frames while Visual Studio is paused.")]
    public static Task<string> GetStackTrace(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("get_stack_trace", cancellationToken: ct);

    [McpServerTool(Name = "get_variables", ReadOnly = true)]
    [Description("Returns local variables for a selected stack frame while paused.")]
    public static Task<string> GetVariables(
        WorkerClient client,
        [Description("1-based stack frame index.")] int frameIndex = 1,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "get_variables",
            new JsonObject { ["frameIndex"] = frameIndex },
            cancellationToken: ct);

    [McpServerTool(Name = "evaluate_expression", ReadOnly = true)]
    [Description("Evaluates a C# expression in the current debugger context while paused.")]
    public static Task<string> EvaluateExpression(
        WorkerClient client,
        string expression,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "evaluate_expression",
            new JsonObject { ["expression"] = expression },
            cancellationToken: ct);

    [McpServerTool(Name = "get_current_exception", ReadOnly = true)]
    [Description(
        "Inspects the exception associated with the current stop, including type, message, " +
        "stack trace, and inner exception.")]
    public static Task<string> GetCurrentException(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("get_current_exception", cancellationToken: ct);

    [McpServerTool(Name = "inspect", ReadOnly = true)]
    [Description(
        "Returns debugger state, stack trace, locals, current exception, and requested expression " +
        "evaluations in one call. Can optionally continue after capturing the snapshot.")]
    public static Task<string> Inspect(
        WorkerClient client,
        [Description("Expressions to evaluate in the current debugger context.")] string[]? expressions = null,
        [Description("1-based stack frame index for local variables.")] int frameIndex = 1,
        [Description("Resume execution after collecting the snapshot.")] bool autoContinue = false,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "inspect",
            new JsonObject
            {
                ["expressions"] = new JsonArray(
                    (expressions ?? [])
                    .Select(value => (JsonNode?)JsonValue.Create(value))
                    .ToArray()),
                ["frameIndex"] = frameIndex,
                ["autoContinue"] = autoContinue
            },
            timeoutSeconds: 120,
            cancellationToken: ct);

    [McpServerTool(Name = "continue_execution")]
    [Description("Continues execution from the current debugger stop.")]
    public static Task<string> Continue(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("continue", cancellationToken: ct);

    [McpServerTool(Name = "pause_execution")]
    [Description("Breaks a running debuggee, equivalent to Visual Studio Break All.")]
    public static Task<string> Pause(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("pause", timeoutSeconds: 120, cancellationToken: ct);

    [McpServerTool(Name = "step_over")]
    [Description("Steps over the current statement and waits for the next debugger stop.")]
    public static Task<string> StepOver(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("step_over", timeoutSeconds: 180, cancellationToken: ct);

    [McpServerTool(Name = "step_into")]
    [Description("Steps into the current statement and waits for the next debugger stop.")]
    public static Task<string> StepInto(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("step_into", timeoutSeconds: 180, cancellationToken: ct);

    [McpServerTool(Name = "step_out")]
    [Description("Steps out of the current function and waits for the next debugger stop.")]
    public static Task<string> StepOut(WorkerClient client, CancellationToken ct) =>
        client.InvokeAsync("step_out", timeoutSeconds: 180, cancellationToken: ct);

    [McpServerTool(Name = "attach_to_process")]
    [Description("Attaches the selected Visual Studio debugger to an existing local process ID.")]
    public static Task<string> Attach(
        WorkerClient client,
        int processId,
        [Description("Disable Visual Studio Just My Code after attaching.")] bool disableJustMyCode = false,
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "attach_to_process",
            new JsonObject
            {
                ["processId"] = processId,
                ["disableJustMyCode"] = disableJustMyCode
            },
            timeoutSeconds: 180,
            cancellationToken: ct);

    [McpServerTool(Name = "set_exception_settings")]
    [Description(
        "Changes Visual Studio exception settings for one exception in an exception group.")]
    public static Task<string> SetExceptionSettings(
        WorkerClient client,
        string exceptionName,
        bool breakWhenThrown,
        bool breakWhenUserUnhandled,
        string groupName = "Common Language Runtime Exceptions",
        CancellationToken ct = default) =>
        client.InvokeAsync(
            "set_exception_settings",
            new JsonObject
            {
                ["groupName"] = groupName,
                ["exceptionName"] = exceptionName,
                ["breakWhenThrown"] = breakWhenThrown,
                ["breakWhenUserUnhandled"] = breakWhenUserUnhandled
            },
            cancellationToken: ct);
}
