using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualStudioDebuggerMcp;

internal sealed class WorkerClient
{
    private const int StateProbeTimeoutSeconds = 20;

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

    public async Task<string> WaitForDebugModeAsync(
        string expectedMode,
        int timeoutSeconds,
        int pollMilliseconds,
        CancellationToken cancellationToken)
    {
        var validMode = new[] { "Run", "Break", "Design" }
            .FirstOrDefault(mode =>
                string.Equals(mode, expectedMode, StringComparison.OrdinalIgnoreCase));
        if (validMode is null)
        {
            throw new ArgumentException(
                "Expected debugger mode must be Run, Break, or Design.",
                nameof(expectedMode));
        }

        if (timeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutSeconds),
                "Timeout must be zero or greater.");
        }

        if (pollMilliseconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollMilliseconds),
                "Polling interval must be greater than zero.");
        }

        var deadline = timeoutSeconds > 0
            ? DateTime.UtcNow.AddSeconds(timeoutSeconds)
            : (DateTime?)null;
        var sawBuildInProgress = false;

        while (deadline is null || DateTime.UtcNow < deadline)
        {
            var probeTimeoutSeconds = deadline is null
                ? StateProbeTimeoutSeconds
                : Math.Max(
                    1,
                    Math.Min(
                        StateProbeTimeoutSeconds,
                        (int)Math.Ceiling((deadline.Value - DateTime.UtcNow).TotalSeconds)));
            var stateJson = await InvokeAsync(
                "get_state",
                timeoutSeconds: probeTimeoutSeconds,
                cancellationToken: cancellationToken);
            var state = JsonNode.Parse(stateJson)?.AsObject()
                ?? throw new InvalidOperationException(
                    "Visual Studio state probe returned invalid JSON.");
            var buildState = state["buildState"]?.GetValue<string>();
            if (string.Equals(
                buildState,
                "vsBuildStateInProgress",
                StringComparison.Ordinal))
            {
                sawBuildInProgress = true;
            }

            var failedProjects = state["lastBuildFailedProjects"]?.GetValue<int>() ?? 0;
            if (sawBuildInProgress &&
                string.Equals(buildState, "vsBuildStateDone", StringComparison.Ordinal) &&
                failedProjects > 0)
            {
                throw new InvalidOperationException(
                    $"Visual Studio build completed with {failedProjects} failed project(s).");
            }

            var actualMode = state["debugMode"]?.GetValue<string>();
            if (string.Equals(actualMode, validMode, StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject
                {
                    ["debugMode"] = actualMode,
                    ["reached"] = true,
                    ["state"] = state
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            if (string.Equals(validMode, "Run", StringComparison.Ordinal) &&
                string.Equals(actualMode, "Break", StringComparison.Ordinal))
            {
                return new JsonObject
                {
                    ["debugMode"] = actualMode,
                    ["reached"] = false,
                    ["blocked"] = true,
                    ["state"] = state
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            await Task.Delay(pollMilliseconds, cancellationToken);
        }

        throw new TimeoutException(
            $"Visual Studio did not reach debugger mode '{expectedMode}' within the configured " +
            $"{timeoutSeconds}-second limit.");
    }

    private static string PrettyPrint(string json)
    {
        var node = JsonNode.Parse(json);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
    }
}
