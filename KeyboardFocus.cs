using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace WpfAutomationMcp;

internal sealed record FocusCheck(string status, string? reason = null, object? expected = null,
    object? actual = null, string? readError = null)
{
    public static FocusCheck Compare(object expected, object actual, string mismatch) =>
        Equals(expected, actual) ? new("passed", expected: expected, actual: actual)
            : new("failed", mismatch, expected, actual);
}

internal sealed class KeyboardOperation(string operationId, long deadlineTick)
{
    public const int WaitLimitMs = 350;
    public const int CheckIntervalMs = 50;

    public string OperationId { get; } = operationId;
    public long RemainingMs => deadlineTick - Environment.TickCount64;
    public string Phase { get; set; } = "resolve_target";
    public string TextWriteStatus { get; set; } = "not_started";
    public string SendStatus { get; set; } = "not_sent";
    public int? TargetPid { get; set; }
    public long? TargetHwnd { get; set; }
    public int CheckCount { get; set; }
    public double WaitElapsedMs { get; set; }
    public string? FailureReason { get; set; }
    public string? BlockingCheck { get; set; }
    public string? CheckPhase { get; set; }
    public Dictionary<string, FocusCheck> Checks { get; } = new()
    {
        ["foregroundWindow"] = new("not_checked"),
        ["foregroundPid"] = new("not_checked"),
        ["uiaFocus"] = new("not_checked"),
        ["focusPid"] = new("not_checked"),
        ["focusAutomationId"] = new("not_checked"),
        ["focusControlType"] = new("not_checked"),
        ["targetControl"] = new("not_checked"),
        ["targetKeyboardFocus"] = new("not_checked"),
        ["windowScope"] = new("not_checked"),
        ["documentScope"] = new("not_applicable", "document_context_not_supplied"),
        ["modalScope"] = new("not_checked")
    };

    public object Report() => new
    {
        operationId = OperationId, operation = "set_text", phase = Phase, keys = new[] { "Enter" },
        textWriteStatus = TextWriteStatus, sendStatus = SendStatus, diagnosticsAvailable = true,
        expected = new { pid = TargetPid, hwnd = TargetHwnd },
        failureReason = FailureReason, blockingCheck = BlockingCheck, checkPhase = CheckPhase, checks = Checks,
        waitElapsedMs = WaitElapsedMs, checkCount = CheckCount, waitLimitMs = WaitLimitMs,
        checkIntervalMs = CheckIntervalMs
    };

    public static bool Requested(string method, JsonElement arguments) =>
        method == "set_text" && arguments.TryGetProperty("pressEnter", out var flag) && flag.ValueKind == JsonValueKind.True;

    public void ThrowIfExpired()
    {
        if (RemainingMs > 0) return;
        FailureReason = "worker_timeout";
        throw new TimeoutException("Keyboard operation exceeded the worker deadline. Read current state before retrying.");
    }
}

public sealed partial class UiaSession
{
    private object SetText(JsonElement args, KeyboardOperation? keyboard)
    {
        if (keyboard != null) keyboard.TargetPid = ProcessId;
        var element = Find(args, out var root, out var modal)!;
        if (!element.IsEnabled) throw new InvalidOperationException("Element is disabled.");
        if (!element.Patterns.Value.TryGetPattern(out var value) || value.IsReadOnly.Value)
            throw new NotSupportedException("Element has no writable ValuePattern. Add an AutomationPeer/IValueProvider.");
        if (keyboard == null)
        {
            value.SetValue(Text(args, "text") ?? "");
            return new { pattern = "Value", enterSent = false, state = Snapshot(element) };
        }

        var hwnd = root.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
        {
            keyboard.FailureReason = "target_window_unavailable";
            throw new InvalidOperationException("Cannot read the target window handle.");
        }
        keyboard.TargetHwnd = hwnd.ToInt64();
        keyboard.Checks["modalScope"] = modal ? new("not_checked") : new("not_applicable", "modal_context_not_supplied");

        keyboard.Phase = "before_focus";
        if (!WaitForKeyboardFocus(keyboard, root, element, hwnd, modal, foregroundOnly: true))
            ThrowIfFocusBlocked(keyboard, "focus_wait_timeout");
        keyboard.Phase = "set_focus";
        keyboard.ThrowIfExpired();
        element.FrameworkAutomationElement.SetFocus();
        keyboard.Phase = "before_write";
        CheckKeyboardFocus(keyboard, root, element, hwnd, modal, foregroundOnly: false);
        ThrowIfFocusBlocked(keyboard);
        keyboard.ThrowIfExpired();

        keyboard.Phase = "write_value";
        keyboard.TextWriteStatus = "unknown";
        value.SetValue(Text(args, "text") ?? "");
        keyboard.TextWriteStatus = "written";

        keyboard.Phase = "before_send";
        var settled = WaitForKeyboardFocus(keyboard, root, element, hwnd, modal, foregroundOnly: false);
        keyboard.Phase = "final_check";
        keyboard.ThrowIfExpired();
        CheckKeyboardFocus(keyboard, root, element, hwnd, modal, foregroundOnly: false);
        ThrowIfFocusBlocked(keyboard, settled ? null : "focus_wait_timeout");
        keyboard.ThrowIfExpired();

        keyboard.Phase = "send";
        keyboard.SendStatus = "unknown";
        Keyboard.Type(VirtualKeyShort.RETURN);
        keyboard.SendStatus = "sent";
        keyboard.Phase = "readback";
        var state = Snapshot(element);
        keyboard.Phase = "complete";
        return new { pattern = "Value", enterSent = true, state, keyboard = keyboard.Report() };
    }

    private bool WaitForKeyboardFocus(KeyboardOperation keyboard, AutomationElement root, AutomationElement target,
        IntPtr hwnd, bool modal, bool foregroundOnly)
    {
        var wait = Stopwatch.StartNew();
        try
        {
            do
            {
                keyboard.ThrowIfExpired();
                CheckKeyboardFocus(keyboard, root, target, hwnd, modal, foregroundOnly);
                if (keyboard.BlockingCheck == null) return true;
                var pause = Math.Min(KeyboardOperation.WaitLimitMs - wait.ElapsedMilliseconds, keyboard.RemainingMs);
                if (pause <= 0) break;
                Thread.Sleep((int)Math.Min(pause, KeyboardOperation.CheckIntervalMs));
            } while (wait.ElapsedMilliseconds < KeyboardOperation.WaitLimitMs);

            keyboard.ThrowIfExpired();
            return false;
        }
        finally { keyboard.WaitElapsedMs += wait.Elapsed.TotalMilliseconds; }
    }

    private static void ThrowIfFocusBlocked(KeyboardOperation keyboard, string? failureReason = null)
    {
        if (keyboard.BlockingCheck is not { } check) return;
        keyboard.FailureReason = failureReason ?? keyboard.Checks[check].reason;
        throw new InvalidOperationException(keyboard.TextWriteStatus == "written"
            ? "Text was set but focus could not be verified; Enter was NOT sent. Read current state before retrying."
            : "pressEnter requires verified target window and focus; no text changed.");
    }

    private void CheckKeyboardFocus(KeyboardOperation keyboard, AutomationElement root, AutomationElement target,
        IntPtr hwnd, bool modal, bool foregroundOnly)
    {
        keyboard.CheckCount++;
        keyboard.CheckPhase = keyboard.Phase;
        var checks = keyboard.Checks;
        var foreground = GetForegroundWindow();
        checks["foregroundWindow"] = foreground == IntPtr.Zero
            ? new("failed", "foreground_window_unavailable", hwnd.ToInt64())
            : FocusCheck.Compare(hwnd.ToInt64(), foreground.ToInt64(), "foreground_window_mismatch");
        checks["foregroundPid"] = foreground == IntPtr.Zero
            ? new("not_checked", "foreground_window_unavailable", keyboard.TargetPid)
            : GetWindowThreadProcessId(foreground, out int pid) == 0 || pid <= 0
                ? new("failed", "foreground_pid_unavailable", keyboard.TargetPid)
                : FocusCheck.Compare(keyboard.TargetPid!.Value, pid, "foreground_pid_mismatch");

        // A matching foreground is sufficient here only to allow the existing SetFocus.
        // Sample the UIA properties on a failed foreground check as evidence for the caller.
        if (!foregroundOnly || checks["foregroundWindow"].status != "passed" || checks["foregroundPid"].status != "passed")
        {
            var focus = ReadFocus(out var readReason);
            checks["uiaFocus"] = focus == null
                ? new("failed", "uia_focus_unavailable", "available", readError: readReason)
                : new("passed", expected: "available", actual: "available");
            checks["focusPid"] = ReadFocusProperty(focus?.Properties.ProcessId, keyboard.TargetPid,
                "focus_pid_unavailable", "focus_pid_mismatch");
            checks["focusAutomationId"] = ReadFocusProperty(focus?.Properties.AutomationId, null, "focus_automation_id_unavailable");
            checks["focusControlType"] = ReadFocusProperty(focus?.Properties.ControlType, null, "focus_control_type_unavailable");
            if (!foregroundOnly)
            {
                checks["targetControl"] = CheckUia(focus, "same UIA element",
                    () => automation.Compare(target, focus!), "target_control_mismatch", "target_control_unavailable");
                checks["targetKeyboardFocus"] = ReadFocusProperty(target.Properties.HasKeyboardFocus, true,
                    "target_keyboard_focus_unavailable", "target_keyboard_focus_mismatch");
                checks["windowScope"] = CheckUia(focus, "resolved target window",
                    () => IsWindowEnabled(hwnd) && InWindowScope(keyboard, root, focus!),
                    "window_scope_mismatch", "window_scope_unavailable");
                if (modal)
                {
                    checks["modalScope"] = CheckUia(focus, "resolved modal window",
                        () => checks["windowScope"].status == "passed" &&
                            root.Patterns.Window.TryGetPattern(out var dialog) && dialog.IsModal.Value,
                        "modal_scope_mismatch", "modal_scope_unavailable");
                }
            }
        }

        // UIA reads can take time. Re-read native foreground after them as the last focus probe.
        var after = GetForegroundWindow();
        if (after != foreground)
        {
            checks["foregroundWindow"] = new("failed", "foreground_window_changed", hwnd.ToInt64(), after == IntPtr.Zero ? null : after.ToInt64());
            checks["foregroundPid"] = after == IntPtr.Zero
                ? new("not_checked", "foreground_window_unavailable", keyboard.TargetPid)
                : GetWindowThreadProcessId(after, out int afterPid) == 0 || afterPid <= 0
                    ? new("failed", "foreground_pid_unavailable", keyboard.TargetPid)
                    : FocusCheck.Compare(keyboard.TargetPid!.Value, afterPid, "foreground_pid_mismatch");
        }

        string[] required = foregroundOnly ? ["foregroundWindow", "foregroundPid"] :
            ["foregroundWindow", "foregroundPid", "uiaFocus", "focusPid", "targetControl",
             "targetKeyboardFocus", "modalScope", "windowScope"];
        keyboard.BlockingCheck = required.FirstOrDefault(key => checks[key].status is "failed" or "not_checked");
    }

    private AutomationElement? ReadFocus(out string? reason)
    {
        try
        {
            var focus = automation.FocusedElement();
            reason = focus == null ? "no_focused_element" : null;
            return focus;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return null;
        }
    }

    private static FocusCheck ReadFocusProperty<T>(IAutomationProperty<T>? property, object? expected,
        string unavailable, string? mismatch = null)
    {
        if (property == null) return new("not_checked", "uia_focus_unavailable", expected);
        try
        {
            if (!property.TryGetValue(out var value) || value == null || value is int pid && pid <= 0)
                return new("failed", unavailable, expected);
            object actual = value is Enum ? value.ToString()! : value;
            return mismatch == null ? new("passed", actual: actual) : FocusCheck.Compare(expected!, actual, mismatch);
        }
        catch (Exception ex)
        {
            return new("failed", unavailable, expected, readError: ex.GetType().Name);
        }
    }

    private static FocusCheck CheckUia(AutomationElement? focus, string expected, Func<bool> matches,
        string mismatch, string unavailable)
    {
        if (focus == null) return new("not_checked", "uia_focus_unavailable", expected);
        try { return matches() ? new("passed", expected: expected, actual: true) : new("failed", mismatch, expected, false); }
        catch (Exception ex) { return new("failed", unavailable, expected, readError: ex.GetType().Name); }
    }

    private bool InWindowScope(KeyboardOperation keyboard, AutomationElement root, AutomationElement focus)
    {
        if (automation.Compare(root, focus)) return true;
        var walker = automation.TreeWalkerFactory.GetRawViewWalker();
        var ancestor = walker.GetParent(focus);
        for (var depth = 0; ancestor != null && depth < 48; depth++)
        {
            if (keyboard.RemainingMs <= 0) throw new TimeoutException();
            if (automation.Compare(root, ancestor)) return true;
            ancestor = walker.GetParent(ancestor);
        }
        if (ancestor != null) throw new InvalidOperationException("UIA ancestry limit reached.");
        return false;
    }
}
