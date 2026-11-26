using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace WpfAutomationMcp;

// A hung third-party UIA provider must not hang the MCP protocol loop. Only the
// private UIA worker is terminated on timeout; the user's application is untouched.
public sealed class UiaWorker : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? worker;

    public async Task<string> Call(string method, object arguments, int timeoutMs, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(arguments);
        var operationId = KeyboardOperation.Requested(method, payload) ? Guid.NewGuid().ToString("N") : null;
        try { await gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException ex) when (operationId != null)
        {
            return WorkerError(ex, operationId, "worker_cancelled", dispatched: false);
        }
        bool dispatched = false;
        try
        {
            if (worker == null || worker.HasExited)
            {
                worker?.Dispose();
                worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uia-worker")
                {
                    UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Cannot start the UIA worker.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var request = JsonSerializer.Serialize(new { method, arguments = payload, operationId,
                deadlineTick = Environment.TickCount64 + timeoutMs });
            deadline.Token.ThrowIfCancellationRequested();
            // A failed write or flush may still have handed a complete request to the worker.
            dispatched = true;
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token);
            await worker.StandardInput.FlushAsync(deadline.Token);
            var read = worker.StandardOutput.ReadLineAsync(deadline.Token).AsTask();
            // A worker-launched client can inherit the pipe handle and delay EOF after worker exit.
            var exited = worker.WaitForExitAsync(deadline.Token);
            try
            {
                var completed = await Task.WhenAny(read, exited);
                if (completed == exited && !read.IsCompletedSuccessfully)
                {
                    await exited;
                    throw new IOException("UIA worker exited. Call launch_or_attach again.");
                }
                return await read ?? throw new IOException("UIA worker pipe closed. Call launch_or_attach again.");
            }
            finally { deadline.Cancel(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            var workerExited = worker?.HasExited == true;
            StopWorker();
            var reason = ex is IOException || workerExited ? "worker_pipe_failure" :
                cancellationToken.IsCancellationRequested ? "worker_cancelled" : "worker_timeout";
            return WorkerError(ex, operationId, reason, dispatched);
        }
        finally { gate.Release(); }
    }

    private static string WorkerError(Exception ex, string? operationId, string reason, bool dispatched)
    {
        const string recovery = "Application state may have changed; inspect it after launch_or_attach before retrying an action.";
        var error = !dispatched && operationId != null ? "Keyboard request did not reach the UIA worker." :
            "UIA worker stopped after timeout/cancellation or pipe failure. " + recovery;
        if (operationId == null) return JsonSerializer.Serialize(new { ok = false, error, detail = ex.Message });
        return JsonSerializer.Serialize(new { ok = false, error, detail = ex.Message, keyboard = new
        {
            operationId, operation = "set_text", phase = dispatched ? "worker_response_unavailable" : "before_dispatch",
            keys = new[] { "Enter" }, diagnosticsAvailable = false, diagnosticsMissingReason = reason, failureReason = reason,
            textWriteStatus = dispatched ? "unknown" : "not_started", sendStatus = dispatched ? "unknown" : "not_sent"
        } });
    }

    private void StopWorker()
    {
        if (worker == null) return;
        try
        {
            if (!worker.HasExited) worker.Kill(); // The app may be a child and must survive.
        }
        catch (InvalidOperationException) when (worker.HasExited) { }
        catch (Win32Exception) when (worker.HasExited) { }
        finally
        {
            worker.Dispose();
            worker = null;
        }
    }

    public void Dispose() => StopWorker();
}

[McpServerToolType]
public sealed class WpfTools(UiaWorker worker)
{
    [McpServerTool(Name = "launch_or_attach"), Description("Attach to a unique process matching exePath/processName, or launch exePath. Checks actual bitness. Returns PID, ownership and active window. Does not close existing applications.")]
    public Task<string> LaunchOrAttach(string? exePath = null, string? args = null, string? processName = null, CancellationToken cancellationToken = default) =>
        worker.Call("launch_or_attach", new { exePath, args, processName }, 35000, cancellationToken);

    [McpServerTool(Name = "get_ui_tree", ReadOnly = true), Description("Compact UIA control tree of the attached application's active/modal window. Max depth 1-12, 120 nodes, 16000 characters, 5 seconds traversal. Reports truncation. No screenshots.")]
    public Task<string> GetUiTree(int maxDepth = 4, bool interactiveOnly = true, CancellationToken cancellationToken = default) =>
        worker.Call("get_ui_tree", new { maxDepth, interactiveOnly }, 10000, cancellationToken);

    [McpServerTool(Name = "click_element"), Description("Unique exact locator, ANDs supplied AutomationId/Name/ControlType. Uses Invoke, Toggle, SelectionItem or ExpandCollapse. No coordinate input; missing patterns require an AutomationPeer.")]
    public Task<string> ClickElement(string? automationId = null, string? name = null, string? controlType = null, CancellationToken cancellationToken = default) =>
        worker.Call("click_element", new { automationId, name, controlType }, 10000, cancellationToken);

    [McpServerTool(Name = "set_text"), Description("Set text using writable ValuePattern. Optional pressEnter sends Enter only after verifying foreground window and keyboard focus on the exact element. Password controls are never read back.")]
    public Task<string> SetText(string text, string? automationId = null, string? name = null, bool pressEnter = false, string? controlType = null, CancellationToken cancellationToken = default) =>
        worker.Call("set_text", new { automationId, name, text, pressEnter, controlType }, 10000, cancellationToken);

    [McpServerTool(Name = "wait_for_element", ReadOnly = true), Description("Poll for a unique, visible and enabled element in active/modal window. timeoutMs 1-30000; explicit timeout/ambiguous errors. Optional ControlType disambiguates Name.")]
    public Task<string> WaitForElement(string? automationId = null, string? name = null, int timeoutMs = 5000, string? controlType = null, CancellationToken cancellationToken = default) =>
        worker.Call("wait_for_element", new { automationId, name, timeoutMs, controlType }, Math.Clamp(timeoutMs, 1, 30000) + 5000, cancellationToken);

    [McpServerTool(Name = "get_element_state", ReadOnly = true), Description("Read a unique element's value, toggle/selection state and supported patterns. GridPattern reads at most 10 rows x 8 columns, with totals/truncation. Virtualized/offscreen cells may be unavailable.")]
    public Task<string> GetElementState(string? automationId = null, string? name = null, string? controlType = null, CancellationToken cancellationToken = default) =>
        worker.Call("get_element_state", new { automationId, name, controlType }, 10000, cancellationToken);

    [McpServerTool(Name = "wait_for_window", ReadOnly = true), Description("Wait for a unique visible enabled top-level window with exact native title in the attached process. Uses native discovery only, no UIA tree traversal or focus change. Returns native-visible-enabled, NOT UIA/control readiness; query the next control before acting. timeoutMs 1-30000.")]
    public Task<string> WaitForWindow(string title, int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        worker.Call("wait_for_window", new { title, timeoutMs }, Math.Clamp(timeoutMs, 1, 30000) + 5000, cancellationToken);
}
