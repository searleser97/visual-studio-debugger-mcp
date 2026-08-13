using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol.Types;
using ModelContextProtocol.Server;
using VisualStudioDebuggerMcp;

if (args.Length > 0 && string.Equals(args[0], "--worker", StringComparison.Ordinal))
{
    await WorkerHost.RunAsync(args);
    return;
}

Console.SetOut(Console.Error);

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddSingleton<WorkerClient>()
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "visual-studio-debugger",
            Version = "0.2.0"
        };
        options.ServerInstructions =
            "Generic Visual Studio debugger automation for Windows. Select a Visual Studio instance, " +
            "build or start debugging, manage breakpoints, poll status, inspect stack frames and variables, " +
            "report and resolve blocking modal dialogs, evaluate expressions, inspect exceptions, step, " +
            "continue, pause, attach, and stop. Long-running " +
            "state changes are initiated asynchronously and observed through short status probes. Every " +
            "EnvDTE operation runs in an isolated timeout-bounded worker process. Before inspecting, " +
            "evaluating, continuing, or stepping, call get_visual_studio_state and proceed only when " +
            "debugMode is Break; breakReason identifies why execution paused.";
    })
    .WithStdioServerTransport()
    .WithTools<VisualStudioTools>();

await builder.Build().RunAsync();
