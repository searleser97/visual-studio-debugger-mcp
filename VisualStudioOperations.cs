using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnvDTE;
using EnvDTE80;

namespace VisualStudioDebuggerMcp;

internal static class VisualStudioOperations
{
    private const int MaxStackFrames = 50;
    private const int MaxVariables = 300;
    private const int ExpressionTimeoutMilliseconds = 5000;

    public static bool UsesEnvDte(JsonObject request) =>
        request["operation"]?.GetValue<string>() is not ("start_visual_studio" or "get_port_status");

    public static string Execute(JsonObject request)
    {
        var operation = request["operation"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Worker request has no operation.");
        var arguments = request["arguments"]?.AsObject() ?? new JsonObject();

        return operation switch
        {
            "list_instances" => ListInstances(),
            "start_visual_studio" => StartVisualStudio(arguments),
            "get_state" => WithInstance(arguments, GetState),
            "apply_debugger_settings" => WithInstance(arguments, ApplyDebuggerSettings),
            "start_build" => WithInstance(arguments, StartBuild),
            "get_build_status" => WithInstance(arguments, GetBuildStatus),
            "start_debugging" => WithInstance(arguments, StartDebugging),
            "get_port_status" => GetPortStatus(arguments).ToJsonString(),
            "stop_debugging" => WithInstance(arguments, StopDebugging),
            "close_visual_studio" => WithInstance(arguments, CloseVisualStudio),
            "set_breakpoint" => WithInstance(arguments, SetBreakpoint),
            "remove_breakpoint" => WithInstance(arguments, RemoveBreakpoint),
            "list_breakpoints" => WithInstance(arguments, ListBreakpoints),
            "get_stack_trace" => WithInstance(arguments, GetStackTrace),
            "get_variables" => WithInstance(arguments, GetVariables),
            "evaluate_expression" => WithInstance(arguments, EvaluateExpression),
            "get_current_exception" => WithInstance(arguments, GetCurrentException),
            "inspect" => WithInstance(arguments, Inspect),
            "continue" => WithInstance(arguments, Continue),
            "pause" => WithInstance(arguments, Pause),
            "step_over" => WithInstance(arguments, (instance, _) => Step(instance, "over")),
            "step_into" => WithInstance(arguments, (instance, _) => Step(instance, "into")),
            "step_out" => WithInstance(arguments, (instance, _) => Step(instance, "out")),
            "attach_to_process" => WithInstance(arguments, AttachToProcess),
            "set_exception_settings" => WithInstance(arguments, SetExceptionSettings),
            _ => throw new InvalidOperationException($"Unknown worker operation '{operation}'.")
        };
    }

    private static string WithInstance(
        JsonObject arguments,
        Func<VisualStudioInstance, JsonObject, JsonNode> operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                var instance = VsConnection.Find(
                    arguments["solutionPattern"]?.GetValue<string>(),
                    arguments["visualStudioProcessId"]?.GetValue<int>());
                return operation(instance, arguments).ToJsonString();
            }
            catch (System.Runtime.InteropServices.COMException) when (DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(250);
            }
        }
    }

    private static string ListInstances()
    {
        var instances = new JsonArray();
        foreach (var instance in VsConnection.GetAll())
        {
            instances.Add(new JsonObject
            {
                ["processId"] = instance.ProcessId,
                ["solution"] = instance.Solution,
                ["moniker"] = instance.Moniker
            });
        }

        return new JsonObject { ["instances"] = instances }.ToJsonString();
    }

    private static string StartVisualStudio(JsonObject arguments)
    {
        var executable = RequiredString(arguments, "visualStudioExecutable");
        var solution = RequiredString(arguments, "solutionPath");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The configured Visual Studio executable was not found.", executable);
        }

        if (!File.Exists(solution))
        {
            throw new FileNotFoundException("The configured solution was not found.", solution);
        }

        var process = System.Diagnostics.Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Arguments = $"\"{solution}\""
        }) ?? throw new InvalidOperationException("Visual Studio did not start.");

        return new JsonObject
        {
            ["started"] = true,
            ["processId"] = process.Id,
            ["solutionPath"] = solution,
            ["solutionPattern"] = $"*{Path.GetFileName(solution)}"
        }.ToJsonString();
    }

    private static JsonNode GetState(VisualStudioInstance instance, JsonObject arguments)
    {
        var dte = instance.Dte;
        var debugger = (Debugger2)dte.Debugger;
        var mode = debugger.CurrentMode;
        var lastBuildFailedProjects = 0;
        try
        {
            lastBuildFailedProjects = dte.Solution.SolutionBuild.LastBuildInfo;
        }
        catch (COMException)
        {
            // Visual Studio throws before the solution has completed its first build.
        }

        var state = new JsonObject
        {
            ["processId"] = instance.ProcessId,
            ["solution"] = instance.Solution,
            ["buildState"] = dte.Solution.SolutionBuild.BuildState.ToString(),
            ["lastBuildFailedProjects"] = lastBuildFailedProjects,
            ["debugMode"] = mode switch
            {
                dbgDebugMode.dbgBreakMode => "Break",
                dbgDebugMode.dbgRunMode => "Run",
                dbgDebugMode.dbgDesignMode => "Design",
                _ => "Unknown"
            },
            ["statusText"] = dte.StatusBar.Text
        };

        if (mode == dbgDebugMode.dbgBreakMode)
        {
            state["breakReason"] = debugger.LastBreakReason.ToString();
        }

        return state;
    }

    private static JsonNode ApplyDebuggerSettings(
        VisualStudioInstance instance,
        JsonObject arguments)
    {
        var disableJustMyCode = arguments["disableJustMyCode"]?.GetValue<bool>() ?? false;
        ApplyDebuggerSettings(instance.Dte, disableJustMyCode);
        return new JsonObject
        {
            ["disableJustMyCode"] = disableJustMyCode,
            ["applied"] = true
        };
    }

    private static JsonNode StartBuild(VisualStudioInstance instance, JsonObject arguments)
    {
        ActivateConfiguration(
            instance.Dte,
            arguments["solutionConfiguration"]?.GetValue<string>());
        instance.Dte.Solution.SolutionBuild.Build(WaitForBuildToFinish: false);
        return new JsonObject
        {
            ["started"] = true
        };
    }

    private static JsonNode GetBuildStatus(VisualStudioInstance instance, JsonObject arguments)
    {
        _ = arguments;
        try
        {
            var solutionBuild = instance.Dte.Solution.SolutionBuild;
            var buildState = solutionBuild.BuildState;
            var failedProjects = 0;
            try
            {
                failedProjects = solutionBuild.LastBuildInfo;
            }
            catch (COMException)
            {
                // Visual Studio throws before the solution has completed its first build.
            }

            var status = buildState switch
            {
                vsBuildState.vsBuildStateInProgress => "Running",
                vsBuildState.vsBuildStateDone when failedProjects > 0 => "Failed",
                vsBuildState.vsBuildStateDone => "Succeeded",
                _ => "NotStarted"
            };

            return new JsonObject
            {
                ["status"] = status,
                ["buildState"] = buildState.ToString(),
                ["failedProjects"] = failedProjects
            };
        }
        catch (COMException exception) when (
            exception.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A))
        {
            return new JsonObject
            {
                ["status"] = "Running",
                ["buildState"] = "VisualStudioBusy",
                ["failedProjects"] = 0
            };
        }
    }

    private static JsonNode StartDebugging(VisualStudioInstance instance, JsonObject arguments)
    {
        ApplyDebuggerSettings(
            instance.Dte,
            arguments["disableJustMyCode"]?.GetValue<bool>() ?? false);
        ActivateConfiguration(
            instance.Dte,
            arguments["solutionConfiguration"]?.GetValue<string>());
        if (arguments["startupProjects"] is JsonArray startupProjects && startupProjects.Count > 0)
        {
            instance.Dte.Solution.SolutionBuild.StartupProjects =
                startupProjects.Select(project => project!.GetValue<string>()).ToArray();
        }

        instance.Dte.ExecuteCommand(
            arguments["launchCommand"]?.GetValue<string>() ?? "Debug.Start");
        return new JsonObject { ["started"] = true };
    }

    private static JsonNode GetPortStatus(JsonObject arguments)
    {
        var host = arguments["host"]?.GetValue<string>() ?? "127.0.0.1";
        var ports = RequiredPorts(arguments);
        var connectTimeoutMilliseconds =
            arguments["connectTimeoutMilliseconds"]?.GetValue<int>() ?? 500;
        var statuses = new JsonArray();
        foreach (var port in ports)
        {
            statuses.Add(new JsonObject
            {
                ["host"] = host,
                ["port"] = port,
                ["ready"] = IsPortReady(host, port, connectTimeoutMilliseconds)
            });
        }

        return new JsonObject { ["ports"] = statuses };
    }

    private static JsonNode StopDebugging(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = (Debugger2)instance.Dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
        {
            debugger.Stop(WaitForDesignMode: false);
        }

        return new JsonObject { ["stopRequested"] = true };
    }

    private static JsonNode CloseVisualStudio(VisualStudioInstance instance, JsonObject arguments)
    {
        var saveAll = arguments["saveAll"]?.GetValue<bool>() ?? true;
        var debugger = (Debugger2)instance.Dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
        {
            debugger.Stop(WaitForDesignMode: true);
        }

        if (saveAll)
        {
            instance.Dte.ExecuteCommand("File.SaveAll");
        }

        instance.Dte.Quit();
        return new JsonObject { ["closed"] = true, ["processId"] = instance.ProcessId };
    }

    private static JsonNode SetBreakpoint(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = (Debugger2)instance.Dte.Debugger;
        var file = ResolveFile(instance, RequiredString(arguments, "file"));
        var line = RequiredInt(arguments, "line");
        var column = arguments["column"]?.GetValue<int>() ?? 1;
        var condition = arguments["condition"]?.GetValue<string>() ?? string.Empty;

        foreach (Breakpoint breakpoint in debugger.Breakpoints)
        {
            if (string.Equals(breakpoint.File, file, StringComparison.OrdinalIgnoreCase) &&
                breakpoint.FileLine == line)
            {
                breakpoint.Delete();
                break;
            }
        }

        debugger.Breakpoints.Add(
            File: file,
            Line: line,
            Column: column,
            Condition: condition,
            ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
        return new JsonObject
        {
            ["set"] = true,
            ["file"] = file,
            ["line"] = line,
            ["condition"] = condition
        };
    }

    private static JsonNode RemoveBreakpoint(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = (Debugger2)instance.Dte.Debugger;
        var file = ResolveFile(instance, RequiredString(arguments, "file"));
        var line = RequiredInt(arguments, "line");
        var removed = false;
        foreach (Breakpoint breakpoint in debugger.Breakpoints)
        {
            if (string.Equals(breakpoint.File, file, StringComparison.OrdinalIgnoreCase) &&
                breakpoint.FileLine == line)
            {
                breakpoint.Delete();
                removed = true;
                break;
            }
        }

        return new JsonObject { ["removed"] = removed, ["file"] = file, ["line"] = line };
    }

    private static JsonNode ListBreakpoints(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = (Debugger2)instance.Dte.Debugger;
        var breakpoints = new JsonArray();
        foreach (Breakpoint breakpoint in debugger.Breakpoints)
        {
            breakpoints.Add(new JsonObject
            {
                ["file"] = breakpoint.File,
                ["line"] = breakpoint.FileLine,
                ["column"] = breakpoint.FileColumn,
                ["enabled"] = breakpoint.Enabled,
                ["condition"] = breakpoint.Condition
            });
        }

        return new JsonObject { ["breakpoints"] = breakpoints };
    }

    private static JsonNode GetStackTrace(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = RequireBreak(instance);
        var frames = new JsonArray();
        var index = 1;
        foreach (EnvDTE.StackFrame frame in debugger.CurrentThread.StackFrames)
        {
            frames.Add(new JsonObject
            {
                ["index"] = index++,
                ["function"] = frame.FunctionName,
                ["module"] = frame.Module
            });
            if (index > MaxStackFrames)
            {
                break;
            }
        }

        return new JsonObject { ["frames"] = frames };
    }

    private static JsonNode GetVariables(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = RequireBreak(instance);
        var frameIndex = arguments["frameIndex"]?.GetValue<int>() ?? 1;
        var frame = (EnvDTE.StackFrame)debugger.CurrentThread.StackFrames.Item(frameIndex);
        var variables = new JsonArray();
        var count = 0;
        foreach (Expression local in frame.Locals)
        {
            variables.Add(ExpressionToJson(local));
            if (++count >= MaxVariables)
            {
                break;
            }
        }

        return new JsonObject { ["frameIndex"] = frameIndex, ["variables"] = variables };
    }

    private static JsonNode EvaluateExpression(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = RequireBreak(instance);
        return ExpressionToJson(
            debugger.GetExpression(
                RequiredString(arguments, "expression"),
                UseAutoExpandRules: false,
                Timeout: ExpressionTimeoutMilliseconds));
    }

    private static JsonNode GetCurrentException(VisualStudioInstance instance, JsonObject arguments) =>
        ReadCurrentException(RequireBreak(instance));

    private static JsonNode Inspect(VisualStudioInstance instance, JsonObject arguments)
    {
        var result = new JsonObject
        {
            ["state"] = GetState(instance, arguments),
            ["stackTrace"] = GetStackTrace(instance, arguments),
            ["variables"] = GetVariables(instance, arguments),
            ["exception"] = GetCurrentException(instance, arguments)
        };
        var evaluations = new JsonArray();
        if (arguments["expressions"] is JsonArray expressions)
        {
            foreach (var expression in expressions)
            {
                var childArguments = new JsonObject
                {
                    ["expression"] = expression?.GetValue<string>() ?? string.Empty
                };
                evaluations.Add(EvaluateExpression(instance, childArguments));
            }
        }

        result["evaluations"] = evaluations;
        if (arguments["autoContinue"]?.GetValue<bool>() == true)
        {
            ((Debugger2)instance.Dte.Debugger).Go(WaitForBreakOrEnd: false);
            result["continued"] = true;
        }

        return result;
    }

    private static JsonNode Continue(VisualStudioInstance instance, JsonObject arguments)
    {
        ((Debugger2)instance.Dte.Debugger).Go(WaitForBreakOrEnd: false);
        return new JsonObject { ["continued"] = true };
    }

    private static JsonNode Pause(VisualStudioInstance instance, JsonObject arguments)
    {
        var debugger = (Debugger2)instance.Dte.Debugger;
        if (debugger.CurrentMode == dbgDebugMode.dbgRunMode)
        {
            debugger.Break(WaitForBreakMode: false);
        }

        return new JsonObject { ["pauseRequested"] = true };
    }

    private static JsonNode Step(VisualStudioInstance instance, string kind)
    {
        var debugger = RequireBreak(instance);
        switch (kind)
        {
            case "over":
                debugger.StepOver(WaitForBreakOrEnd: false);
                break;
            case "into":
                debugger.StepInto(WaitForBreakOrEnd: false);
                break;
            case "out":
                debugger.StepOut(WaitForBreakOrEnd: false);
                break;
        }

        return new JsonObject { ["step"] = kind, ["started"] = true };
    }

    private static JsonNode AttachToProcess(VisualStudioInstance instance, JsonObject arguments)
    {
        var processId = RequiredInt(arguments, "processId");
        var debugger = (Debugger2)instance.Dte.Debugger;
        foreach (EnvDTE.Process process in debugger.LocalProcesses)
        {
            if (process.ProcessID == processId)
            {
                process.Attach();
                ApplyDebuggerSettings(
                    instance.Dte,
                    arguments["disableJustMyCode"]?.GetValue<bool>() ?? false);
                return new JsonObject
                {
                    ["attached"] = true,
                    ["processId"] = processId,
                    ["name"] = process.Name
                };
            }
        }

        throw new InvalidOperationException($"Process {processId} is not available to the Visual Studio debugger.");
    }

    private static JsonNode SetExceptionSettings(VisualStudioInstance instance, JsonObject arguments)
    {
        dynamic debugger = instance.Dte.Debugger;
        var groupName = arguments["groupName"]?.GetValue<string>() ?? "Common Language Runtime Exceptions";
        var exceptionName = RequiredString(arguments, "exceptionName");
        var group = debugger.ExceptionGroups.Item(groupName);
        var exception = group.Item(exceptionName);
        group.SetBreakWhenThrown(arguments["breakWhenThrown"]?.GetValue<bool>() ?? false, exception);
        group.SetBreakWhenUserUnhandled(
            arguments["breakWhenUserUnhandled"]?.GetValue<bool>() ?? false,
            exception);
        return new JsonObject
        {
            ["group"] = groupName,
            ["exception"] = exceptionName,
            ["updated"] = true
        };
    }

    private static void ApplyDebuggerSettings(DTE2 dte, bool disableJustMyCode)
    {
        if (disableJustMyCode)
        {
            try
            {
                dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value = false;
            }
            catch (ArgumentException)
            {
                // Some Visual Studio SKUs do not expose this property through EnvDTE.
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Debugger settings can be temporarily unavailable while a session is transitioning.
            }
        }
    }

    private static void ActivateConfiguration(DTE2 dte, string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return;
        }

        foreach (SolutionConfiguration candidate in dte.Solution.SolutionBuild.SolutionConfigurations)
        {
            if (string.Equals(candidate.Name, configuration, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Activate();
                return;
            }
        }
    }

    private static JsonObject ReadCurrentException(Debugger2 debugger)
    {
        var root = debugger.GetExpression(
            "$exception",
            UseAutoExpandRules: false,
            Timeout: ExpressionTimeoutMilliseconds);
        if (!root.IsValidValue)
        {
            return new JsonObject { ["available"] = false, ["value"] = root.Value };
        }

        return new JsonObject
        {
            ["available"] = true,
            ["type"] = EvaluateValue(debugger, "$exception.GetType().FullName"),
            ["message"] = EvaluateValue(debugger, "$exception.Message"),
            ["stackTrace"] = EvaluateValue(debugger, "$exception.StackTrace"),
            ["innerException"] = EvaluateValue(debugger, "$exception.InnerException")
        };
    }

    private static string EvaluateValue(Debugger2 debugger, string expression)
    {
        var value = debugger.GetExpression(
            expression,
            UseAutoExpandRules: false,
            Timeout: ExpressionTimeoutMilliseconds);
        return value.Value;
    }

    private static Debugger2 RequireBreak(VisualStudioInstance instance)
    {
        var debugger = (Debugger2)instance.Dte.Debugger;
        if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
        {
            throw new InvalidOperationException("Visual Studio must be in Break mode for this operation.");
        }

        return debugger;
    }

    private static JsonObject ExpressionToJson(Expression expression) => new()
    {
        ["name"] = expression.Name,
        ["type"] = expression.Type,
        ["value"] = expression.Value,
        ["isValid"] = expression.IsValidValue
    };

    private static string ResolveFile(VisualStudioInstance instance, string file) =>
        Path.IsPathRooted(file)
            ? file
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(instance.Solution)!, file));

    private static string RequiredString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>()
        ?? throw new InvalidOperationException($"Argument '{name}' is required.");

    private static int RequiredInt(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<int>()
        ?? throw new InvalidOperationException($"Argument '{name}' is required.");

    private static int[] RequiredPorts(JsonObject arguments)
    {
        if (arguments["ports"] is not JsonArray ports || ports.Count == 0)
        {
            throw new InvalidOperationException("Argument 'ports' must contain at least one TCP port.");
        }

        return ports.Select(port => port!.GetValue<int>()).ToArray();
    }

    private static bool IsPortReady(string host, int port, int timeoutMilliseconds)
    {
        if (host is "localhost" or "127.0.0.1" or "::1")
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }

        try
        {
            using var client = new TcpClient();
            client.ConnectAsync(host, port)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds))
                .GetAwaiter()
                .GetResult();
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureVisualStudioIsRunning(VisualStudioInstance instance)
    {
        if (instance.ProcessId is not int processId)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Visual Studio process {processId} exited.");
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Visual Studio process {processId} is no longer running.");
        }
    }
}
