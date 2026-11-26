using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace WpfAutomationFixture;

internal sealed class KeyboardDiagnosticsFixture : Window
{
    public KeyboardDiagnosticsFixture(string evidence, bool blocker)
    {
        if (!Path.IsPathFullyQualified(evidence)) throw new ArgumentException("Absolute evidence path required.");
        void Record(string eventName, object? detail = null) => File.AppendAllText(evidence,
            JsonSerializer.Serialize(new { eventName, detail }) + Environment.NewLine);

        Title = blocker ? "WPF keyboard diagnostics blocker" : "WPF keyboard diagnostics fixture";
        AutomationProperties.SetAutomationId(this, blocker ? "Fixture.Keyboard.Blocker" : "Fixture.Keyboard.Main");
        Width = 650;
        Height = 350;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(16) };
        Content = root;
        var wrong = new TextBox { Text = "unrelated editor" };
        AutomationProperties.SetAutomationId(wrong, "Fixture.Keyboard.Wrong");
        root.Children.Add(wrong);
        wrong.GotKeyboardFocus += (_, _) => Record("wrong_focus");
        Loaded += (_, _) =>
        {
            Activate();
            wrong.Focus();
            Record("ready", new { pid = Environment.ProcessId, window = new WindowInteropHelper(this).Handle.ToInt64(), active = IsActive });
        };
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == Key.Enter) Record("enter_received");
        }), true);

        if (blocker) return;

        var editor = new TextBox { Text = "initial" };
        AutomationProperties.SetAutomationId(editor, "Fixture.Keyboard.Editor");
        root.Children.Add(editor);
        var submission = new TextBox { Text = "none", IsReadOnly = true };
        AutomationProperties.SetAutomationId(submission, "Fixture.Keyboard.Submission");
        root.Children.Add(submission);
        var shiftOnWrite = false;
        editor.GotKeyboardFocus += (_, _) => Record("target_focus");
        editor.TextChanged += (_, _) =>
        {
            Record("value_written", new { targetHasKeyboardFocus = editor.IsKeyboardFocused });
            if (!shiftOnWrite) return;
            shiftOnWrite = false;
            // ValuePattern.SetValue cannot return until this synchronous focus transfer has run.
            Record("focus_transferred", new { wrongHasKeyboardFocus = wrong.Focus() && wrong.IsKeyboardFocused });
        };
        editor.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            submission.Text = editor.Text;
            Record("submitted");
        };

        var arm = new Button { Content = "Focus other editor after next text write" };
        AutomationProperties.SetAutomationId(arm, "Fixture.Keyboard.Arm");
        arm.Click += (_, _) =>
        {
            wrong.Focus();
            shiftOnWrite = true;
            Record("armed", new { wrongHasKeyboardFocus = wrong.IsKeyboardFocused });
        };
        root.Children.Add(arm);

        var showBlocker = new Button { Content = "Open separate test foreground window" };
        AutomationProperties.SetAutomationId(showBlocker, "Fixture.Keyboard.OpenBlocker");
        showBlocker.Click += (_, _) =>
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                ArgumentList = { "--keyboard-blocker", Path.Combine(Path.GetDirectoryName(evidence)!, "blocker.jsonl") }
            });
            Record("blocker_started", new { pid = child!.Id });
        };
        root.Children.Add(showBlocker);
    }
}
