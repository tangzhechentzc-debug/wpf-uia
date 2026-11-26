using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfAutomationMcp;

if (args.Contains("--uia-worker"))
{
    // This entry point stays on one MTA thread; UIA COM objects never cross threads.
    using var session = new UiaSession();
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        KeyboardOperation? keyboard = null;
        try
        {
            using var request = JsonDocument.Parse(line);
            var envelope = request.RootElement;
            var method = envelope.GetProperty("method").GetString()!;
            var arguments = envelope.GetProperty("arguments");
            if (KeyboardOperation.Requested(method, arguments))
                keyboard = new KeyboardOperation(envelope.GetProperty("operationId").GetString()!,
                    envelope.GetProperty("deadlineTick").GetInt64());
            var result = session.Execute(method, arguments, keyboard);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, elapsedMs = clock.Elapsed.TotalMilliseconds, result }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.StackTrace}");
            var response = new { ok = false, elapsedMs = clock.Elapsed.TotalMilliseconds,
                error = ex.GetType().Name + ": " + ex.Message, processId = session.ProcessId,
                startedByServer = session.StartedByServer, stability = (ex as TreeTransitionException)?.Stability };
            if (keyboard == null) Console.WriteLine(JsonSerializer.Serialize(response));
            else
            {
                keyboard.FailureReason ??= keyboard.Phase switch
                {
                    "set_focus" => "set_focus_exception",
                    "write_value" => "text_write_exception",
                    "send" => "send_exception",
                    "readback" => "readback_exception",
                    "resolve_target" => "target_resolution_failed",
                    _ => "focus_check_exception"
                };
                Console.WriteLine(JsonSerializer.Serialize(new { response.ok, response.elapsedMs, response.error,
                    response.processId, response.startedByServer, response.stability, keyboard = keyboard.Report() }));
            }
        }
    }
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<UiaWorker>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<WpfTools>();
await builder.Build().RunAsync();
