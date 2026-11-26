using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using ElementNotAvailableException = FlaUI.Core.Exceptions.ElementNotAvailableException;
using FlaUI.UIA3;
using FlaUI.UIA3.Converters;
using FlaUI.UIA3.Extensions;

namespace WpfAutomationMcp;

public sealed partial class UiaSession : IDisposable
{
    private readonly UIA3Automation automation = new()
    {
        ConnectionTimeout = TimeSpan.FromSeconds(2), TransactionTimeout = TimeSpan.FromSeconds(2)
    };
    private Process? process;
    public int? ProcessId => process?.Id;
    public bool StartedByServer { get; private set; }

    internal object Execute(string method, JsonElement args, KeyboardOperation? keyboard) => method switch
    {
        "launch_or_attach" => Launch(Text(args, "exePath"), Text(args, "args"), Text(args, "processName")),
        "get_ui_tree" => Tree(args.GetProperty("maxDepth").GetInt32(), args.GetProperty("interactiveOnly").GetBoolean()),
        "get_element_state" => State(Find(args)!),
        "click_element" => Click(Find(args)!),
        "set_text" => SetText(args, keyboard),
        "wait_for_element" => Wait(args, args.GetProperty("timeoutMs").GetInt32()),
        "wait_for_window" => WaitForWindow(Text(args, "title"), args.GetProperty("timeoutMs").GetInt32()),
        _ => throw new ArgumentException("Unknown tool: " + method)
    };

    private object Launch(string? exePath, string? args, string? processName)
    {
        if (string.IsNullOrWhiteSpace(exePath) && string.IsNullOrWhiteSpace(processName))
            throw new ArgumentException("exePath or processName is required.");
        var path = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetFullPath(exePath);
        var name = string.IsNullOrWhiteSpace(processName) ? Path.GetFileNameWithoutExtension(path!) : Path.GetFileName(processName);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var candidates = Process.GetProcessesByName(name);
        var matches = new List<Process>();
        foreach (var candidate in candidates)
        {
            if (path == null || string.Equals(candidate.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
            else candidate.Dispose();
        }
        if (matches.Count > 1)
        {
            foreach (var match in matches) match.Dispose();
            throw new InvalidOperationException("Ambiguous process: multiple matching instances. Supply a unique executable path or close the extra test instance.");
        }
        if (matches.Count == 1)
        {
            var alreadyOwned = process?.Id == matches[0].Id && StartedByServer;
            process?.Dispose();
            process = matches[0];
            StartedByServer = alreadyOwned;
        }
        else
        {
            if (path == null || !File.Exists(path)) throw new FileNotFoundException("Launch executable does not exist.", path);
            process?.Dispose();
            process = Process.Start(new ProcessStartInfo(path, args ?? "")
            {
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Process could not be started.");
            StartedByServer = true;
            // Keep application output away from the worker's JSON stream.
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        CheckArchitecture(process);
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 25000)
        {
            if (process.HasExited) throw new InvalidOperationException($"Process {process.Id} exited ({process.ExitCode}). Check startup logs or an x86 relaunch; attach to the actual executable.");
            var window = ActiveWindow(false);
            if (window != null) return new { processId = process.Id, startedByServer = StartedByServer,
                architecture = Environment.Is64BitProcess ? "x64" : "x86", window = WindowInfo(window) };
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Process {process.Id} has no visible window after 25000 ms.");
    }

    private static void CheckArchitecture(Process target)
    {
        if (!IsWow64Process(target.Handle, out var wow64)) throw new System.ComponentModel.Win32Exception();
        var target64 = Environment.Is64BitOperatingSystem && !wow64;
        if (target64 != Environment.Is64BitProcess)
            throw new InvalidOperationException($"Architecture mismatch: target is {(target64 ? "x64" : "x86")}; MCP is {(Environment.Is64BitProcess ? "x64" : "x86")}. Build with -r win-{(target64 ? "x64" : "x86")} and register that executable.");
    }

    private AutomationElement? ActiveWindow(bool required = true, bool probeTreeRoot = false) =>
        ActiveWindow(out _, required, probeTreeRoot);

    private AutomationElement? ActiveWindow(out bool modal, bool required = true, bool probeTreeRoot = false)
    {
        modal = false;
        if (process == null || process.HasExited) throw new InvalidOperationException("No live attachment. Call launch_or_attach first.");
        CheckArchitecture(process);
        var handles = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == process.Id && IsWindowVisible(handle)) handles.Add(handle);
            return true;
        }, IntPtr.Zero);
        var foreground = GetForegroundWindow();
        // Prefer an owned modal over its disabled owner, even when another app has focus.
        foreach (var handle in handles.OrderByDescending(h => h == foreground))
        {
            if (!IsWindowEnabled(handle)) continue;
            var element = WindowFromHandle(handle, probeTreeRoot);
            if (element.Patterns.Window.TryGetPattern(out var pattern) && pattern.IsModal.ValueOrDefault && element.IsEnabled)
            {
                modal = true;
                return element;
            }
        }
        var chosen = handles.FirstOrDefault(h => h == foreground && IsWindowEnabled(h));
        if (chosen == IntPtr.Zero) chosen = handles.FirstOrDefault(h => IsWindowEnabled(h) && GetWindow(h, 4) != IntPtr.Zero);
        if (chosen == IntPtr.Zero) chosen = handles.FirstOrDefault(IsWindowEnabled);
        if (chosen != IntPtr.Zero) return WindowFromHandle(chosen, probeTreeRoot);
        if (required) throw new InvalidOperationException("No enabled visible window for the attached process.");
        return null;
    }

    private AutomationElement WindowFromHandle(IntPtr handle, bool probeTreeRoot)
    {
        if (!probeTreeRoot) return automation.FromHandle(handle);

        var connectionTimeout = automation.ConnectionTimeout;
        var transactionTimeout = automation.TransactionTimeout;
        var probeTimeout = TimeSpan.FromMilliseconds(250);
        try
        {
            automation.ConnectionTimeout = probeTimeout;
            automation.TransactionTimeout = probeTimeout;
            return automation.FromHandle(handle);
        }
        catch (TimeoutException ex)
        {
            throw new TreeRootTimeoutException(ex);
        }
        finally
        {
            automation.ConnectionTimeout = connectionTimeout;
            automation.TransactionTimeout = transactionTimeout;
        }
    }

    private static object WindowInfo(AutomationElement element) => new
    {
        handle = element.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(),
        title = Clip(element.Properties.Name.ValueOrDefault, 160),
        automationId = element.Properties.AutomationId.ValueOrDefault
    };

    private object Tree(int maxDepth, bool interactiveOnly)
    {
        if (maxDepth is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(maxDepth), "Expected 1-12.");
        const int retryWindowMs = 1200;
        const int rootTimeoutRetryWindowMs = 5000;
        const int uiaElementNotAvailable = unchecked((int)0x80040201);
        int attempts = 0, noEnabledWindow = 0, elementUnavailable = 0, rootTimeouts = 0;
        int waitBudgetMs = retryWindowMs;
        Stopwatch? recovery = null;
        var timing = new TreeTiming();
        do
        {
            attempts++;
            try
            {
                AutomationElement? root;
                var phase = Stopwatch.GetTimestamp();
                try { root = ActiveWindow(required: false, probeTreeRoot: true); }
                finally { timing.rootResolveMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds; }
                if (root != null)
                {
                    phase = Stopwatch.GetTimestamp();
                    try { return ReadTree(root, maxDepth, interactiveOnly, new { attempts, noEnabledWindow, elementUnavailable, rootTimeouts }, timing); }
                    finally { timing.readMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds; }
                }
                noEnabledWindow++;
            }
            catch (TreeRootTimeoutException)
            {
                rootTimeouts++;
                waitBudgetMs = rootTimeoutRetryWindowMs;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException ||
                                       ex is COMException { ErrorCode: uiaElementNotAvailable })
            {
                elementUnavailable++;
            }
            // Re-enumerate on the next attempt. Never continue walking a stale root or return its partial tree.
            recovery ??= Stopwatch.StartNew();
            var remaining = waitBudgetMs - (int)recovery.ElapsedMilliseconds;
            if (remaining <= 0) break;
            var pause = Stopwatch.GetTimestamp();
            Thread.Sleep(Math.Min(75, remaining));
            timing.retryDelayMs += Stopwatch.GetElapsedTime(pause).TotalMilliseconds;
        } while (recovery.ElapsedMilliseconds < waitBudgetMs);

        throw new TreeTransitionException(new { attempts, noEnabledWindow, elementUnavailable, rootTimeouts });
    }

    private void CheckTreeWindow(IntPtr handle)
    {
        if (GetWindowThreadProcessId(handle, out var pid) == 0 || pid != ProcessId ||
            !IsWindowVisible(handle) || !IsWindowEnabled(handle))
            throw new ElementNotAvailableException();
    }

    private object ReadTree(AutomationElement root, int maxDepth, bool interactiveOnly, object stability, TreeTiming timing)
    {
        var handle = root.Properties.NativeWindowHandle.ValueOrDefault;
        CheckTreeWindow(handle);
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        var props = automation.PropertyLibrary;
        cache.Add(props.Element.AutomationId);
        cache.Add(props.Element.NativeWindowHandle);
        cache.Add(props.Element.Name);
        cache.Add(props.Element.ControlType);
        cache.Add(props.Element.IsEnabled);
        cache.Add(props.Element.IsOffscreen);
        cache.Add(props.Element.IsPassword);
        cache.Add(props.Value.Value);
        cache.Add(props.RangeValue.Value);
        var available = props.PatternAvailability;
        cache.Add(available.IsValuePatternAvailable);
        cache.Add(available.IsRangeValuePatternAvailable);
        cache.Add(available.IsInvokePatternAvailable);
        cache.Add(available.IsTogglePatternAvailable);
        cache.Add(available.IsSelectionItemPatternAvailable);
        cache.Add(available.IsExpandCollapsePatternAvailable);
        cache.Add(available.IsGridPatternAvailable);
        using var cached = cache.Activate();
        var phase = Stopwatch.GetTimestamp();
        try { root = WindowFromHandle(handle, probeTreeRoot: true); }
        finally { timing.rootCacheMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds; }
        var window = WindowInfo(root);
        var nodes = new List<object>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var timer = Stopwatch.StartNew();
        int visited = 0, size = 0;
        bool truncated = false;
        var walker = ((UIA3TreeWalker)automation.TreeWalkerFactory.GetControlViewWalker()).NativeTreeWalker;
        // FlaUI recreates the native cache configuration on every walker call.
        // This traversal uses an immutable request, so reuse it within this read only.
        var nativeCache = cache.ToNative(automation);
        while (queue.Count > 0)
        {
            if (nodes.Count >= 120 || visited >= 600 || timer.ElapsedMilliseconds >= 5000) { truncated = true; break; }
            var (element, depth) = queue.Dequeue();
            visited++;
            phase = Stopwatch.GetTimestamp();
            var type = element.ControlType;
            var meaningful = type is not (ControlType.Pane or ControlType.Group or ControlType.Custom or ControlType.Text or ControlType.Image or ControlType.Separator);
            if (!meaningful && !string.IsNullOrEmpty(element.Properties.Name.ValueOrDefault))
                meaningful = CachedPatternAvailable(element, available.IsInvokePatternAvailable) ||
                    CachedPatternAvailable(element, available.IsTogglePatternAvailable) ||
                    CachedPatternAvailable(element, available.IsSelectionItemPatternAvailable) ||
                    CachedPatternAvailable(element, available.IsExpandCollapsePatternAvailable) ||
                    CachedPatternAvailable(element, available.IsValuePatternAvailable) ||
                    CachedPatternAvailable(element, available.IsGridPatternAvailable);
            if (!element.Properties.IsOffscreen.ValueOrDefault && (!interactiveOnly || meaningful ||
                !string.IsNullOrEmpty(element.Properties.AutomationId.ValueOrDefault)))
            {
                var node = Snapshot(element, scalarCache: true);
                int bytes = JsonSerializer.Serialize(node).Length;
                if (size + bytes > 16000) { truncated = true; break; }
                size += bytes;
                nodes.Add(node);
            }
            timing.projectMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
            phase = Stopwatch.GetTimestamp();
            try
            {
                // Decorators are omitted from output, but their descendants remain searchable.
                var child = automation.WrapNativeElement(walker.GetFirstChildElementBuildCache(element.ToNative(), nativeCache));
                if (depth >= maxDepth) { if (child != null) truncated = true; continue; }
                while (child != null)
                {
                    if (queue.Count + visited >= 600 || timer.ElapsedMilliseconds >= 5000) { truncated = true; break; }
                    queue.Enqueue((child, depth + 1));
                    child = automation.WrapNativeElement(walker.GetNextSiblingElementBuildCache(child.ToNative(), nativeCache));
                }
            }
            finally { timing.navigateMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds; }
        }
        CheckTreeWindow(handle);
        return new { window, nodes, visited, truncated, maxDepth, stability, timing };
    }

    private static object Snapshot(AutomationElement element, bool scalarCache = false) => new
    {
        AutomationId = Clip(element.Properties.AutomationId.ValueOrDefault, 120),
        Name = element.Properties.IsPassword.ValueOrDefault ? "[redacted]" : Clip(element.Properties.Name.ValueOrDefault, 160),
        ControlType = element.ControlType.ToString(), IsEnabled = element.Properties.IsEnabled.ValueOrDefault,
        Value = Value(element, scalarCache)
    };

    private static bool CachedPatternAvailable(AutomationElement element, FlaUI.Core.Identifiers.PropertyId property) =>
        element.FrameworkAutomationElement.TryGetPropertyValue<bool>(property, out var available) && available;

    private static string? Value(AutomationElement element, bool scalarCache)
    {
        if (element.Properties.IsPassword.ValueOrDefault) return "[redacted]";
        if (scalarCache)
        {
            var props = element.Automation.PropertyLibrary;
            var native = element.FrameworkAutomationElement;
            if (CachedPatternAvailable(element, props.PatternAvailability.IsValuePatternAvailable))
                return Clip(native.GetPropertyValue<string>(props.Value.Value), 160);
            if (CachedPatternAvailable(element, props.PatternAvailability.IsRangeValuePatternAvailable))
                return native.GetPropertyValue<double>(props.RangeValue.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }
        if (element.Patterns.Value.TryGetPattern(out var value)) return Clip(value.Value.Value, 160);
        if (element.Patterns.RangeValue.TryGetPattern(out var range)) return range.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    private AutomationElement? Find(JsonElement args, bool optional = false) => Find(args, out _, out _, optional);

    private AutomationElement? Find(JsonElement args, out AutomationElement root, out bool modal, bool optional = false)
    {
        string? id = Text(args, "automationId"), name = Text(args, "name"), typeName = Text(args, "controlType");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Supply AutomationId or exact Name; unqualified actions are forbidden.");
        var conditions = new List<ConditionBase>();
        var cf = automation.ConditionFactory;
        if (!string.IsNullOrWhiteSpace(id)) conditions.Add(cf.ByAutomationId(id));
        if (!string.IsNullOrWhiteSpace(name)) conditions.Add(cf.ByName(name));
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            if (!Enum.TryParse<ControlType>(typeName, true, out var type) || !Enum.IsDefined(type)) throw new ArgumentException("Invalid ControlType: " + typeName);
            conditions.Add(cf.ByControlType(type));
        }
        var condition = conditions.Aggregate((left, right) => left.And(right));
        root = ActiveWindow(out modal)!;
        var matches = root.FindAll(TreeScope.Subtree, condition).Where(e => !e.IsOffscreen).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous locator. Add a unique AutomationId, exact Name or ControlType; no action was performed.");
        if (matches.Length == 0 && !optional) throw new InvalidOperationException("Element not found in active window (a modal may have changed the scope).");
        return matches.FirstOrDefault();
    }

    private object WaitForWindow(string? title, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512)
            throw new ArgumentException("Supply an exact window title of 1-512 characters.");
        if (timeoutMs is < 1 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Expected 1-30000.");
        if (process == null || process.HasExited)
            throw new InvalidOperationException("No live attachment. Call launch_or_attach first.");

        var timer = Stopwatch.StartNew();
        var caption = new StringBuilder(title.Length + 2);
        int polls = 0;
        do
        {
            if (process.HasExited) throw new InvalidOperationException($"Attached process {process.Id} exited while waiting for a window.");
            polls++;
            var matches = new List<IntPtr>();
            // Native discovery avoids contacting a closing window's UIA provider.
            // Visibility is only a checkpoint; callers must query the next control before acting.
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var pid);
                if (pid != process.Id || !IsWindowVisible(handle) || !IsWindowEnabled(handle)) return true;
                GetWindowText(handle, caption, caption.Capacity);
                if (string.Equals(caption.ToString(), title, StringComparison.Ordinal)) matches.Add(handle);
                return true;
            }, IntPtr.Zero);
            if (matches.Count > 1) throw new InvalidOperationException("Ambiguous window title: multiple visible enabled windows in the attached process.");
            if (matches.Count == 1)
            {
                var handle = matches[0];
                GetWindowThreadProcessId(handle, out var pid);
                GetWindowText(handle, caption, caption.Capacity);
                if (pid == process.Id && IsWindowVisible(handle) && IsWindowEnabled(handle) && caption.ToString() == title)
                    return new { processId = process.Id, window = new { handle = handle.ToInt64(), title },
                        readiness = "native-visible-enabled", polls };
            }
            var remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
            if (remaining > 0) Thread.Sleep(Math.Min(50, remaining));
        } while (timer.ElapsedMilliseconds < timeoutMs);
        throw new TimeoutException($"No visible enabled window with exact title '{title}' in process {process.Id} after {timeoutMs} ms ({polls} polls).");
    }

    private object Wait(JsonElement args, int timeoutMs)
    {
        if (timeoutMs is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Expected 1-30000.");
        var timer = Stopwatch.StartNew();
        do
        {
            var element = Find(args, true);
            if (element != null && element.IsEnabled) return Snapshot(element);
            var remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
            if (remaining > 0) Thread.Sleep(Math.Min(100, remaining));
        } while (timer.ElapsedMilliseconds < timeoutMs);
        throw new TimeoutException($"Element absent or disabled after {timeoutMs} ms (AutomationId={Text(args, "automationId")}, Name={Text(args, "name")}).");
    }

    private object Click(AutomationElement element)
    {
        if (!element.IsEnabled) throw new InvalidOperationException("Element is disabled.");
        string used;
        if (element.Patterns.Invoke.TryGetPattern(out var invoke)) { invoke.Invoke(); used = "Invoke"; }
        else if (element.Patterns.Toggle.TryGetPattern(out var toggle)) { toggle.Toggle(); used = "Toggle"; }
        else if (element.Patterns.SelectionItem.TryGetPattern(out var selection)) { selection.Select(); used = "SelectionItem"; }
        else if (element.Patterns.ExpandCollapse.TryGetPattern(out var expand))
        {
            if (expand.ExpandCollapseState.Value == ExpandCollapseState.Expanded) expand.Collapse(); else expand.Expand();
            used = "ExpandCollapse";
        }
        else throw new NotSupportedException("No actionable UIA pattern. Add an AutomationPeer; coordinate fallback is disabled by the repository's no-global-mouse requirement.");
        return new { pattern = used, actionCompleted = true };
    }

    private static object State(AutomationElement element)
    {
        string? toggle = element.Patterns.Toggle.TryGetPattern(out var t) ? t.ToggleState.Value.ToString() : null;
        bool? selected = element.Patterns.SelectionItem.TryGetPattern(out var s) ? s.IsSelected.Value : null;
        object? gridState = null;
        if (element.Patterns.Grid.TryGetPattern(out var grid))
        {
            int rows = grid.RowCount.Value, columns = grid.ColumnCount.Value;
            var cells = new List<object>();
            for (int row = 0; row < Math.Min(rows, 10); row++)
                for (int column = 0; column < Math.Min(columns, 8); column++)
                {
                    try { var cell = grid.GetItem(row, column); cells.Add(new { row, column, state = Snapshot(cell) }); }
                    catch (COMException) { cells.Add(new { row, column, unavailable = true }); }
                }
            gridState = new { rows, columns, cells, truncated = rows > 10 || columns > 8 };
        }
        return new { element = Snapshot(element), toggleState = toggle, isSelected = selected,
            patterns = element.GetSupportedPatterns().Select(p => p.Name).ToArray(), grid = gridState };
    }

    private static string? Text(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Clip(string? value, int length) => value?.Length > length ? value[..length] + "…" : value;
    public void Dispose() { automation.Dispose(); process?.Dispose(); }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    private sealed class TreeRootTimeoutException(TimeoutException innerException)
        : Exception("UIA timed out opening a candidate tree root.", innerException);

    private sealed class TreeTiming
    {
        public double rootResolveMs { get; set; }
        public double rootCacheMs { get; set; }
        public double readMs { get; set; }
        public double navigateMs { get; set; }
        public double projectMs { get; set; }
        public double retryDelayMs { get; set; }
    }
}

internal sealed class TreeTransitionException(object stability)
    : TimeoutException("No stable UIA window was available within the get_ui_tree transition retry window.")
{
    public object Stability { get; } = stability;
}
