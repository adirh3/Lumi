using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "LumiMcp",
            Title = "Lumi MCP",
            Version = typeof(Lumi.Mcp.LumiMcpTools).Assembly.GetName().Version?.ToString() ?? "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<Lumi.Mcp.LumiMcpTools>();

await builder.Build().RunAsync();
