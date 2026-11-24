using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WpfHarness;

internal sealed class MainWindow : CooperativeWindow
{
    public static readonly Size LogicalSurface = new(760, 196);

    public readonly Menu MenuBar = new();
    public readonly ToolBar Tools = new();
    public readonly Canvas Surface = new() { Width = LogicalSurface.Width, Height = LogicalSurface.Height, Background = Brushes.White };
    public readonly Label KnownLabel = new() { Content = "Known size: 240 × 24 logical px", Background = Brushes.LightYellow, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
    public readonly TextBox Text1 = new() { Text = "Editable text 123", VerticalContentAlignment = VerticalAlignment.Center };
    public readonly CheckBox Check = new() { Content = "Check box option", IsChecked = true, VerticalContentAlignment = VerticalAlignment.Center };
    public readonly ComboBox Combo = new();
    public readonly Button ApplyButton = new() { Content = "Apply" };
    public readonly Button DialogButton = new() { Content = "Dialog…" };
    public readonly ListView List = new();
    public readonly Canvas Vector = new() { Background = Brushes.WhiteSmoke, ClipToBounds = true };
    public readonly ContextMenu Context = new();
    public readonly ToolTip Tip = new() { Content = "WPF tooltip (popup HWND)" };
    public readonly MenuItem FileMenu = new() { Header = "_File" };
    public readonly MenuItem RecentMenu = new() { Header = "_Recent" };
    public readonly Dictionary<FrameworkElement, Rect> Logical = new();

    public MainWindow()
    {
        Title = "AppScale WPF harness (cooperative v1)";
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12; // 9 pt

        RecentMenu.Items.Add(new MenuItem { Header = "first.txt" });
        RecentMenu.Items.Add(new MenuItem { Header = "second.txt" });
        FileMenu.Items.Add(new MenuItem { Header = "_Open…" });
        FileMenu.Items.Add(RecentMenu);
        FileMenu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "E_xit" };
        exit.Click += (_, _) => Close();
        FileMenu.Items.Add(exit);
        var view = new MenuItem { Header = "_View" };
        foreach (var (label, dpi) in new[] { ("75%", 72), ("100%", 96), ("125%", 120), ("150%", 144), ("200%", 192), ("250%", 240), ("_Reset to OS DPI", 0) })
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => Native.SendMessage(Hwnd, SetDpiMsg, dpi, 0); // same path as an external sender
            view.Items.Add(mi);
        }
        var help = new MenuItem { Header = "_Help" };
        help.Click += (_, _) => ShowDialogWindow();
        MenuBar.Items.Add(FileMenu);
        if (DpiState.Cooperative) MenuBar.Items.Add(view);
        else Title = "AppScale WPF harness (stock PMv2, not cooperative)";
        MenuBar.Items.Add(help);

        Tools.Items.Add(new Button { Content = "New" });
        Tools.Items.Add(new Button { Content = "Save" });
        Tools.Items.Add(new Separator());
        Tools.Items.Add(new TextBlock { Text = "Zoom:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        var zoom = new ComboBox { Width = 70 };
        zoom.Items.Add("100%"); zoom.Items.Add("150%"); zoom.SelectedIndex = 0;
        Tools.Items.Add(zoom);
        var tray = new ToolBarTray { IsLocked = true };
        tray.ToolBars.Add(Tools);

        foreach (var s in new[] { "Alpha", "Bravo", "Charlie", "Delta" }) Combo.Items.Add(s);
        Combo.SelectedIndex = 1;
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name", Width = 110, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
        grid.Columns.Add(new GridViewColumn { Header = "Value", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Value") });
        List.View = grid;
        for (int i = 1; i <= 40; i++) List.Items.Add(new { Name = $"Item {i}", Value = i * 37 % 101 });

        foreach (var s in new[] { "Cu_t", "_Copy", "_Paste" }) Context.Items.Add(new MenuItem { Header = s });
        Surface.ContextMenu = Context;
        ApplyButton.ToolTip = Tip;
        DialogButton.Click += (_, _) => ShowDialogWindow();

        // ORIGINAL logical (96-DPI, i.e. DIP) layout.
        Add(KnownLabel, 12, 12, 240, 24);
        Add(Text1, 12, 46, 240, 24);
        Add(Check, 12, 80, 240, 24);
        Add(Combo, 12, 114, 240, 24);
        Add(ApplyButton, 12, 152, 112, 32);
        Add(DialogButton, 140, 152, 112, 32);
        Add(List, 270, 12, 230, 172);
        Add(Vector, 512, 12, 236, 172);
        DrawVectors();

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(MenuBar, Dock.Top);
        DockPanel.SetDock(tray, Dock.Top);
        dock.Children.Add(MenuBar);
        dock.Children.Add(tray);
        dock.Children.Add(Surface);
        Inner.Children.Add(dock);
    }

    private void Add(FrameworkElement e, double x, double y, double w, double h)
    {
        Logical[e] = new Rect(x, y, w, h);
        Place(Surface, e, x, y, w, h);
    }

    private void DrawVectors()
    {
        for (int x = 0; x <= 236; x += 20) Vector.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = 172, Stroke = Brushes.Gainsboro, StrokeThickness = 1 });
        for (int y = 0; y <= 172; y += 20) Vector.Children.Add(new Line { X1 = 0, Y1 = y, X2 = 236, Y2 = y, Stroke = Brushes.Gainsboro, StrokeThickness = 1 });
        var ellipse = new Ellipse { Width = 100, Height = 100, Stroke = Brushes.SteelBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(60, 70, 130, 180)) };
        Canvas.SetLeft(ellipse, 12); Canvas.SetTop(ellipse, 12);
        Vector.Children.Add(ellipse);
        Vector.Children.Add(new Path
        {
            Stroke = Brushes.DarkRed, StrokeThickness = 1.5,
            Data = Geometry.Parse("M 120,150 C 150,20 190,180 226,40"),
        });
        var hair = new Rectangle { Width = 100, Height = 40, Stroke = Brushes.Black, StrokeThickness = 1 };
        Canvas.SetLeft(hair, 124); Canvas.SetTop(hair, 120);
        Vector.Children.Add(hair);
        var t = new TextBlock { Text = "Vector text Ag 12px", FontSize = 12 };
        Canvas.SetLeft(t, 12); Canvas.SetTop(t, 124);
        Vector.Children.Add(t);
        var small = new TextBlock { Text = "Small 9px", FontSize = 9 };
        Canvas.SetLeft(small, 12); Canvas.SetTop(small, 146);
        Vector.Children.Add(small);
    }

    public void ShowDialogWindow()
    {
        var d = new DialogWindow { Owner = this };
        d.ShowDialog();
    }

    protected override object? Metrics()
    {
        if (Hwnd == 0 || !IsLoaded) return null;
        var dpi = VisualTreeHelper.GetDpi(KnownLabel);
        return new
        {
            visualTreeHelperDpi = new { dpi.PixelsPerInchX, dpi.PixelsPerDip },
            knownLabel = new { dipWidth = KnownLabel.ActualWidth, dipHeight = KnownLabel.ActualHeight, physicalPx = PhysicalBounds(KnownLabel, Surface).ToString(), fontSizeDip = KnownLabel.FontSize, logical = "240x24" },
            applyButtonPx = PhysicalBounds(ApplyButton, Surface).ToString(),
            menuHeightPx = Math.Round(MenuBar.ActualHeight * OsDpi / 96.0 * Scale),
            surfacePx = PhysicalBounds(Surface, Surface).ToString(),
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}

internal sealed class DialogWindow : CooperativeWindow
{
    public static readonly Size LogicalClient = new(320, 132);
    public readonly Canvas Surface = new() { Width = LogicalClient.Width, Height = LogicalClient.Height };
    public readonly TextBlock Message = new() { Text = "Modal dialog laid out at the effective DPI.\nThe caption bar stays at the OS DPI.", TextWrapping = TextWrapping.Wrap };
    public readonly Button Ok = new() { Content = "OK", IsDefault = true };
    public readonly Button Cancel = new() { Content = "Cancel", IsCancel = true };
    public readonly Dictionary<FrameworkElement, Rect> Logical = new();

    public DialogWindow()
    {
        Title = "Harness dialog";
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        foreach (var (e, r) in new (FrameworkElement, Rect)[] { (Message, new Rect(12, 12, 296, 60)), (Ok, new Rect(116, 88, 92, 30)), (Cancel, new Rect(216, 88, 92, 30)) })
        {
            Logical[e] = r;
            Place(Surface, e, r.X, r.Y, r.Width, r.Height);
        }
        Ok.Click += (_, _) => DialogResult = true;
        Inner.Children.Add(Surface);
    }
}
