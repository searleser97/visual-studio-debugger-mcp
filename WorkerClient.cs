using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualStudioDebuggerMcp;

internal sealed class WorkerClient
{
    private readonly object targetLock = new();
    private string? solutionPattern;
    private int? visualStudioProcessId;

    public void SetTarget(string? pattern, int? processId)
    {
        lock (this.targetLock)
        {
            this.solutionPattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern;
            this.visualStudioProcessId = processId;
        }
    }

    public async Task<string> InvokeAsync(
        string operation,
        JsonObject? arguments = null,
        int timeoutSeconds = 30,
        bool includeTarget = true,
        CancellationToken cancellationToken = default)
    {
        arguments ??= new JsonObject();
        if (includeTarget)
        {
            lock (this.targetLock)
            {
                if (this.solutionPattern is not null)
                {
                    arguments["solutionPattern"] = this.solutionPattern;
                }

                if (this.visualStudioProcessId is not null)
                {
                    arguments["visualStudioProcessId"] = this.visualStudioProcessId.Value;
                }
            }
        }

        var request = new JsonObject
        {
            ["operation"] = operation,
            ["arguments"] = arguments
        };
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ToJsonString()));
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to locate the MCP executable.");

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add(payload);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the Visual Studio debugger worker.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                $"Visual Studio operation '{operation}' exceeded {timeoutSeconds} seconds. " +
                "The isolated worker was terminated; the MCP remains healthy.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Visual Studio operation '{operation}' failed (exit {process.ExitCode}): " +
                $"{(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        }

        return PrettyPrint(stdout);
    }

    public async Task<string> InvokeStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await InvokeAsync("get_state", cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (
            exception is TimeoutException or InvalidOperationException or Win32Exception)
        {
            string? requestedSolutionPattern;
            int? requestedProcessId;
            lock (this.targetLock)
            {
                requestedSolutionPattern = this.solutionPattern;
                requestedProcessId = this.visualStudioProcessId;
            }

            var timedOut = exception is TimeoutException;
            return new JsonObject
            {
                ["success"] = false,
                ["debuggerStateAvailable"] = false,
                ["automationAvailable"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = timedOut
                        ? "VisualStudioStateTimedOut"
                        : "VisualStudioStateWorkerFailed",
                    ["message"] = exception.Message,
                    ["exceptionType"] = exception.GetType().Name
                },
                ["requestedTarget"] = new JsonObject
                {
                    ["visualStudioProcessId"] = requestedProcessId,
                    ["solutionPattern"] = requestedSolutionPattern
                },
                ["availableInstances"] = new JsonArray(),
                ["availableInstancesUnavailableReason"] =
                    "The isolated EnvDTE worker did not return a state response.",
                ["runningVisualStudioProcesses"] = GetRunningVisualStudioProcesses(),
                ["nextActions"] = new JsonArray(
                    JsonValue.Create(
                        timedOut
                            ? "Retry get_visual_studio_state; another EnvDTE operation or Visual Studio itself may be busy."
                            : "Call list_visual_studio_instances to verify that the selected Visual Studio instance is still registered."),
                    JsonValue.Create(
                        "Inspect Visual Studio for a blocking dialog, then reconnect if the process restarted."))
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    private static JsonArray GetRunningVisualStudioProcesses()
    {
        var processes = new JsonArray();
        foreach (var process in Process.GetProcessesByName("devenv"))
        {
            using (process)
            {
                try
                {
                    processes.Add(new JsonObject
                    {
                        ["processId"] = process.Id,
                        ["mainWindowTitle"] = process.MainWindowTitle,
                        ["responding"] = process.Responding
                    });
                }
                catch (InvalidOperationException)
                {
                    // The process exited while its diagnostic details were being read.
                }
            }
        }

        return processes;
    }

    private static string PrettyPrint(string json)
    {
        var node = JsonNode.Parse(json);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
    }
}
