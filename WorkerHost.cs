using System.Text;
using System.Text.Json.Nodes;

namespace VisualStudioDebuggerMcp;

internal static class WorkerHost
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Worker requires one base64-encoded request.");
            Environment.ExitCode = 2;
            return;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            var request = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("Worker request is not a JSON object.");
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    if (!VisualStudioOperations.UsesEnvDte(request))
                    {
                        completion.SetResult(VisualStudioOperations.Execute(request));
                        return;
                    }

                    using var mutex = new Mutex(
                        initiallyOwned: false,
                        name: "Global\\VisualStudioDebuggerMcp.EnvDte");
                    if (!mutex.WaitOne(TimeSpan.FromMinutes(10)))
                    {
                        throw new TimeoutException(
                            "Timed out waiting for another Visual Studio debugger operation to finish.");
                    }

                    try { completion.SetResult(VisualStudioOperations.Execute(request)); }
                    finally { mutex.ReleaseMutex(); }
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            })
            {
                IsBackground = false,
                Name = "VisualStudioDebuggerWorker-STA"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Console.WriteLine(await completion.Task);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"{exception.GetType().Name} (HResult 0x{exception.HResult:X8}): {exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
            Environment.ExitCode = 1;
        }
    }
}
