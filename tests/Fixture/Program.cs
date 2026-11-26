using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfAutomationFixture;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        if (args.Length == 2 && args[0] is "--keyboard-evidence" or "--keyboard-blocker")
        {
            app.Run(new KeyboardDiagnosticsFixture(args[1], args[0] == "--keyboard-blocker"));
            return;
        }
        app.Run(new FixtureWindow());
    }
}

public sealed class FixtureWindow : Window
{
    private int invocationCount;

    public FixtureWindow()
    {
        Title = "WPF UIA integration fixture";
        Width = 1100;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, "Fixture.Main");
        var root = new StackPanel { Margin = new Thickness(12) };
        Content = root;

        var count = Id(new TextBox { Text = "0", IsReadOnly = true }, "Fixture.Count");
        var invoke = Id(new Button { Content = "Increment", Height = 28 }, "Fixture.Invoke");
        invoke.Click += (_, _) => count.Text = (++invocationCount).ToString();
        root.Children.Add(invoke);
        var custom = new NamedInvokeControl(() => count.Text = (++invocationCount).ToString());
        AutomationProperties.SetName(custom, "Custom action");
        root.Children.Add(custom);
        root.Children.Add(count);
        root.Children.Add(Id(new Slider { Minimum = 0, Maximum = 100, Value = 42, Height = 20 }, "Fixture.Range"));
        root.Children.Add(Id(new CheckBox { Content = "Toggle me", Margin = new Thickness(0, 6, 0, 6) }, "Fixture.Toggle"));

        var enter = Id(new TextBox { Text = "none", IsReadOnly = true }, "Fixture.EnterState");
        var editor = Id(new TextBox { Text = "initial" }, "Fixture.Editor");
        editor.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) enter.Text = "entered:" + editor.Text;
        };
        root.Children.Add(editor);
        root.Children.Add(enter);
        root.Children.Add(Id(new TextBox { Text = "read-only", IsReadOnly = true }, "Fixture.ReadOnly"));
        root.Children.Add(Id(new Button { Content = "Disabled", IsEnabled = false }, "Fixture.Disabled"));
        root.Children.Add(Id(new TextBlock { Text = "No action pattern" }, "Fixture.Decoration"));
        root.Children.Add(Id(new Button { Content = "Duplicate" }, "Fixture.DuplicateA"));
        root.Children.Add(Id(new Button { Content = "Duplicate" }, "Fixture.DuplicateB"));
        root.Children.Add(Id(new PasswordBox { Password = "fixture-secret-never-return" }, "Fixture.Password"));

        var showModal = Id(new Button { Content = "Open modal" }, "Fixture.OpenModal");
        showModal.Click += (_, _) =>
        {
            var close = Id(new Button { Content = "Close dialog", Margin = new Thickness(15) }, "Fixture.Dialog.Close");
            var dialog = Id(new Window
            {
                Title = "UIA fixture modal", Owner = this, Width = 350, Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = close
            }, "Fixture.Dialog");
            close.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
        };
        root.Children.Add(showModal);

        var showNativeModal = Id(new Button { Content = "Open native dialog" }, "Fixture.OpenNativeModal");
        showNativeModal.Click += (_, _) => MessageBox.Show(this, "Native dialog body",
            "UIA native modal fixture", MessageBoxButton.OK, MessageBoxImage.Information);
        root.Children.Add(showNativeModal);

        var tabs = Id(new TabControl { Height = 410, Margin = new Thickness(0, 10, 0, 0) }, "Fixture.Tabs");
        var grid = Id(new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            EnableRowVirtualization = false, EnableColumnVirtualization = false,
            ItemsSource = Enumerable.Range(1, 12).Select(index => new SampleRow($"R{index:00}", index * 10)).ToArray()
        }, "Fixture.Grid");
        grid.Columns.Add(new DataGridTextColumn { Header = "Code", Binding = new System.Windows.Data.Binding(nameof(SampleRow.Code)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Amount", Binding = new System.Windows.Data.Binding(nameof(SampleRow.Amount)) });
        tabs.Items.Add(Id(new TabItem { Header = "Data", Content = grid }, "Fixture.Tab.Data"));
        tabs.Items.Add(Id(new TabItem { Header = "Other", Content = new TextBlock { Text = "second tab" } }, "Fixture.Tab.Other"));
        var bulk = new WrapPanel();
        for (var index = 0; index < 100; index++)
        {
            var button = Id(new Button { Content = $"Item {index}", Width = 98, Height = 32 }, $"Fixture.Bulk.{index}");
            AutomationProperties.SetName(button, $"Item {index} " + new string('X', 400));
            bulk.Children.Add(button);
        }
        tabs.Items.Add(Id(new TabItem { Header = "Bulk", Content = bulk }, "Fixture.Tab.Bulk"));
        root.Children.Add(tabs);
        Loaded += (_, _) => Activate();
    }

    private static T Id<T>(T element, string id) where T : DependencyObject
    {
        AutomationProperties.SetAutomationId(element, id);
        return element;
    }

    public sealed record SampleRow(string Code, int Amount);
}

public sealed class NamedInvokeControl(Action invoke) : FrameworkElement
{
    private readonly Action invokeAction = invoke;

    protected override Size MeasureOverride(Size availableSize) => new(200, 28);

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRoundedRectangle(Brushes.LightBlue, new Pen(Brushes.SteelBlue, 1),
                                            new Rect(RenderSize), 3, 3);
        var text = new FormattedText("Custom action", System.Globalization.CultureInfo.InvariantCulture,
                                     FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12,
                                     Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new Point(8, 5));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NamedInvokePeer(this);

    private sealed class NamedInvokePeer(NamedInvokeControl owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetClassNameCore() => nameof(NamedInvokeControl);
        protected override string GetNameCore() => AutomationProperties.GetName(Owner);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            if (!Owner.IsEnabled) throw new ElementNotEnabledException();
            Owner.Dispatcher.BeginInvoke(((NamedInvokeControl)Owner).invokeAction);
        }
    }
}
