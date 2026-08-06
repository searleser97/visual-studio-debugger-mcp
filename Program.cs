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
            Version = "1.0.0"
        };
        options.ServerInstructions =
            "Generic Visual Studio debugger automation for Windows. Select a Visual Studio instance, " +
            "build or start debugging, manage breakpoints, wait for stops, inspect stack frames and " +
            "variables, evaluate expressions, inspect exceptions, step, continue, pause, attach, and stop. " +
            "Every EnvDTE operation runs in an isolated timeout-bounded worker process.";
    })
    .WithStdioServerTransport()
    .WithTools<VisualStudioTools>();

await builder.Build().RunAsync();
