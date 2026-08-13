using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using EnvDTE80;

namespace VisualStudioDebuggerMcp;

internal sealed record VisualStudioInstance(
    string Moniker,
    int? ProcessId,
    string Solution,
    DTE2 Dte);

internal sealed class VisualStudioConnectionException(
    string code,
    string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal static class VsConnection
{
    public static VisualStudioInstance Find(string? solutionPattern, int? processId)
    {
        var instances = GetAll();
        if (instances.Count == 0)
        {
            throw new VisualStudioConnectionException(
                "NoVisualStudioInstances",
                "No running Visual Studio instances were found.");
        }

        IEnumerable<VisualStudioInstance> matches = instances;
        if (processId is not null)
        {
            matches = matches.Where(instance => instance.ProcessId == processId);
        }

        if (!string.IsNullOrWhiteSpace(solutionPattern))
        {
            matches = matches.Where(instance =>
                FileSystemName.MatchesSimpleExpression(
                    solutionPattern,
                    instance.Solution,
                    ignoreCase: true));
        }

        var selected = matches.ToList();
        if (selected.Count == 1)
        {
            return selected[0];
        }

        if (selected.Count == 0)
        {
            throw new VisualStudioConnectionException(
                "VisualStudioTargetNotFound",
                $"No Visual Studio instance matched processId='{processId}' " +
                $"and solutionPattern='{solutionPattern}'. Running instances: {Describe(instances)}");
        }

        if (string.IsNullOrWhiteSpace(solutionPattern) && processId is null && instances.Count == 1)
        {
            return instances[0];
        }

        throw new VisualStudioConnectionException(
            "AmbiguousVisualStudioTarget",
            $"Multiple Visual Studio instances matched. Specify solutionPattern or visualStudioProcessId. " +
            $"Matches: {Describe(selected)}");
    }

    public static List<VisualStudioInstance> GetAll()
    {
        var result = new List<VisualStudioInstance>();
        GetRunningObjectTable(0, out var rot);
        rot.EnumRunning(out var enumerator);

        try
        {
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IBindCtx? bindContext = null;
                try
                {
                    CreateBindCtx(0, out bindContext);
                    monikers[0].GetDisplayName(bindContext, null, out var displayName);
                    if (!displayName.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    rot.GetObject(monikers[0], out var value);
                    if (value is not DTE2 dte)
                    {
                        continue;
                    }

                    string solution;
                    try
                    {
                        solution = dte.Solution?.FullName ?? string.Empty;
                    }
                    catch
                    {
                        solution = string.Empty;
                    }

                    result.Add(new(
                        displayName,
                        ParseProcessId(displayName),
                        solution,
                        dte));
                }
                catch
                {
                    // A shutting-down or temporarily busy instance is not selectable.
                }
                finally
                {
                    if (bindContext is not null)
                    {
                        Marshal.ReleaseComObject(bindContext);
                    }
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
            Marshal.ReleaseComObject(rot);
        }

        return result;
    }

    private static int? ParseProcessId(string moniker)
    {
        var separator = moniker.LastIndexOf(':');
        return separator >= 0 && int.TryParse(moniker[(separator + 1)..], out var processId)
            ? processId
            : null;
    }

    private static string Describe(IEnumerable<VisualStudioInstance> instances) =>
        string.Join(
            "; ",
            instances.Select(instance =>
                $"PID {instance.ProcessId?.ToString() ?? "unknown"}: " +
                $"{(string.IsNullOrEmpty(instance.Solution) ? "<no solution>" : instance.Solution)}"));

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(
        uint reserved,
        out IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx bindContext);
}
