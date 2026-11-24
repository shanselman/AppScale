namespace WinFormsHarness;

internal sealed class MainForm : CooperativeForm
{
    public const string FontFamilyName = "Segoe UI";
    public const float BaseFontPt = 9f;
    public static readonly Size LogicalContent = new(582, 196);

    public readonly MenuStrip MenuBar = new() { Dock = DockStyle.Top };
    public readonly ToolStrip Tools = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
    public readonly Panel Content = new() { Dock = DockStyle.Fill };
    public readonly Label KnownLabel = new() { AutoSize = false, BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleLeft, Text = "Known size: 240 × 24 logical px" };
    public readonly TextBox Text1 = new() { AutoSize = false, Text = "Editable text 123" };
    public readonly CheckBox Check = new() { AutoSize = false, Text = "Check box option", Checked = true };
    public readonly ComboBox Combo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public readonly Button ApplyButton = new() { Text = "Apply" };
    public readonly Button DialogButton = new() { Text = "Dialog…" };
    public readonly ListView List = new() { View = View.Details, FullRowSelect = true, GridLines = true };
    public readonly ToolTip Tip = new() { OwnerDraw = true, InitialDelay = 300 };
    public readonly ToolTip NativeTip = new() { InitialDelay = 300 };
    public readonly ContextMenuStrip Context = new();
    public readonly ToolStripMenuItem FileMenu = new("&File");
    public readonly ToolStripMenuItem ViewMenu = new("&View");
    public readonly ToolStripMenuItem RecentMenu = new("&Recent");
    private static readonly int[] LogicalColumns = { 150, 120 };

    public MainForm()
    {
        SuspendLayout();
        Text = "AppScale WinForms harness (cooperative v1)";
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        // Menu strip with submenus.
        RecentMenu.DropDownItems.Add("first.txt");
        RecentMenu.DropDownItems.Add("second.txt");
        FileMenu.DropDownItems.Add("&Open…");
        FileMenu.DropDownItems.Add(RecentMenu);
        FileMenu.DropDownItems.Add(new ToolStripSeparator());
        FileMenu.DropDownItems.Add("E&xit", null, (_, _) => Close());
        foreach (var (label, dpi) in new[] { ("75%", 72), ("100%", 96), ("125%", 120), ("150%", 144), ("200%", 192), ("250%", 240), ("&Reset to OS DPI", 0) })
            ViewMenu.DropDownItems.Add(label, null, (_, _) => RequestDpi(dpi));
        MenuBar.Items.Add(FileMenu);
        MenuBar.Items.Add(ViewMenu);
        MenuBar.Items.Add(new ToolStripMenuItem("&Help", null, (_, _) => ShowAbout()));
        MainMenuStrip = MenuBar;

        Tools.Items.Add(new ToolStripButton("New") { DisplayStyle = ToolStripItemDisplayStyle.Text });
        Tools.Items.Add(new ToolStripButton("Save") { DisplayStyle = ToolStripItemDisplayStyle.Text });
        Tools.Items.Add(new ToolStripSeparator());
        Tools.Items.Add(new ToolStripLabel("Zoom:"));
        var zoom = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        zoom.Items.AddRange(new object[] { "100%", "150%" });
        zoom.SelectedIndex = 0;
        Tools.Items.Add(zoom);

        Combo.Items.AddRange(new object[] { "Alpha", "Bravo", "Charlie", "Delta" });
        Combo.SelectedIndex = 1;

        List.Columns.Add("Name", LogicalColumns[0]);
        List.Columns.Add("Value", LogicalColumns[1]);
        for (int i = 1; i <= 40; i++) List.Items.Add(new ListViewItem(new[] { $"Item {i}", (i * 37 % 101).ToString() }));

        Context.Items.Add("Cu&t");
        Context.Items.Add("&Copy");
        Context.Items.Add("&Paste");
        Content.ContextMenuStrip = Context;
        Tip.SetToolTip(ApplyButton, "Tooltip drawn with the effective-DPI font");
        NativeTip.SetToolTip(DialogButton, "Native tooltip (OS tooltip font)");
        Tip.Popup += OnTipPopup;
        Tip.Draw += OnTipDraw;
        DialogButton.Click += (_, _) => ShowAbout();

        // ORIGINAL logical (96-DPI) layout — the only source for every rescale.
        Place(KnownLabel, 12, 12, 240, 24);
        Place(Text1, 12, 46, 240, 24);
        Place(Check, 12, 80, 240, 24);
        Place(Combo, 12, 114, 240, 24);
        Place(ApplyButton, 12, 152, 112, 32);
        Place(DialogButton, 140, 152, 112, 32);
        Place(List, 270, 12, 300, 172);
        Content.Controls.AddRange(new Control[] { KnownLabel, Text1, Check, Combo, ApplyButton, DialogButton, List });

        LogicalFont(this, FontFamilyName, BaseFontPt);
        foreach (Control c in Content.Controls) LogicalFont(c, FontFamilyName, BaseFontPt);
        LogicalFont(MenuBar, FontFamilyName, BaseFontPt);
        LogicalFont(Tools, FontFamilyName, BaseFontPt);
        LogicalFont(Context, FontFamilyName, BaseFontPt);
        LogicalFont(FileMenu.DropDown, FontFamilyName, BaseFontPt);
        LogicalFont(RecentMenu.DropDown, FontFamilyName, BaseFontPt);
        LogicalFont(ViewMenu.DropDown, FontFamilyName, BaseFontPt);
        LogicalFont(zoom.ComboBox, FontFamilyName, BaseFontPt);

        Controls.Add(Content);
        Controls.Add(Tools);
        Controls.Add(MenuBar);
        if (!DpiState.Cooperative)
        {
            // Stock app as the designer would emit it: 96-DPI design, one form font, AutoScaleMode.Dpi does the rest.
            Text = "AppScale WinForms harness (stock PMv2, not cooperative)";
            ViewMenu.Visible = false;
            Tip.OwnerDraw = false;
            StockAutoScale();
            Font = new Font(FontFamilyName, BaseFontPt);
            ClientSize = new Size(LogicalContent.Width, 25 + 25 + LogicalContent.Height);
        }
        ResumeLayout(false);
    }

    protected override object? Metrics() => new
    {
        deviceDpi = DeviceDpi,
        knownLabel = new { KnownLabel.Width, KnownLabel.Height, logical = "240x24", deviceDpi = KnownLabel.DeviceDpi, font = FontInfo(KnownLabel.Font) },
        applyButton = new { ApplyButton.Left, ApplyButton.Top, ApplyButton.Width, ApplyButton.Height },
        list = new { List.Width, List.Height, columns = List.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray(), deviceDpi = List.DeviceDpi },
        menuStripHeight = MenuBar.Height,
        toolStripHeight = Tools.Height,
        contextMenuDeviceDpi = Context.DeviceDpi,
        clientSize = new { ClientSize.Width, ClientSize.Height },
    };

    /// <summary>Interactive View menu: goes through the protocol message exactly like an external sender.</summary>
    private void RequestDpi(int dpi) => Native.SendMessage(Handle, SetDpiMsg, dpi, 0);

    private void ShowAbout()
    {
        using var dlg = new DialogForm();
        dlg.ShowDialog(this);
    }

    protected override void ApplyLayout(int eff)
    {
        for (int i = 0; i < LogicalColumns.Length; i++)
            List.Columns[i].Width = Native.MulDiv(LogicalColumns[i], eff, 96);
        // Images in strips follow ImageScalingSize (set explicitly here). NOTE: measured — this does NOT lift the
        // dropdown/menu item height floor, which comes from private DeviceDpi-scaled fields (_defaultImageSize,
        // paddings) in ToolStripDropDownMenu; those stay at the OS DPI.
        var img = new Size(Native.MulDiv(16, eff, 96), Native.MulDiv(16, eff, 96));
        foreach (ToolStrip s in new ToolStrip[] { MenuBar, Tools, Context, FileMenu.DropDown, RecentMenu.DropDown, ViewMenu.DropDown })
            if (s.ImageScalingSize != img) s.ImageScalingSize = img;
    }

    protected override Size ComputeClientSize(int eff)
    {
        PerformLayout();
        return new Size(Native.MulDiv(LogicalContent.Width, eff, 96),
                        MenuBar.Height + Tools.Height + Native.MulDiv(LogicalContent.Height, eff, 96));
    }

    // The native tooltip control uses the OS (per-monitor) tooltip font; owner-draw lets it follow the effective DPI.
    private void OnTipPopup(object? sender, PopupEventArgs e)
    {
        var font = FontFor(this, EffectiveDpi);
        var text = Tip.GetToolTip(e.AssociatedControl);
        var sz = TextRenderer.MeasureText(text, font);
        int pad = Native.MulDiv(4, EffectiveDpi, 96);
        e.ToolTipSize = new Size(sz.Width + 2 * pad, sz.Height + 2 * pad);
    }

    private void OnTipDraw(object? sender, DrawToolTipEventArgs e)
    {
        e.DrawBackground();
        e.DrawBorder();
        TextRenderer.DrawText(e.Graphics, e.ToolTipText, FontFor(this, EffectiveDpi), e.Bounds, SystemColors.InfoText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class DialogForm : CooperativeForm
{
    public static readonly Size LogicalClient = new(320, 132);
    public readonly Label Message = new() { AutoSize = false, Text = "Modal dialog laid out at the effective DPI.\nThe caption bar stays at the OS DPI." };
    public readonly Button Ok = new() { Text = "OK", DialogResult = DialogResult.OK };
    public readonly Button Cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

    public DialogForm()
    {
        SuspendLayout();
        Text = "Harness dialog";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Place(Message, 12, 12, 296, 60);
        Place(Ok, 116, 88, 92, 30);
        Place(Cancel, 216, 88, 92, 30);
        Controls.AddRange(new Control[] { Message, Ok, Cancel });
        AcceptButton = Ok;
        CancelButton = Cancel;
        LogicalFont(this, MainForm.FontFamilyName, MainForm.BaseFontPt);
        foreach (Control c in Controls) LogicalFont(c, MainForm.FontFamilyName, MainForm.BaseFontPt);
        if (!DpiState.Cooperative)
        {
            StockAutoScale();
            Font = new Font(MainForm.FontFamilyName, MainForm.BaseFontPt);
            ClientSize = LogicalClient;
        }
        ResumeLayout(false);
    }

    protected override object? Metrics() => new { deviceDpi = DeviceDpi, ok = new { Ok.Left, Ok.Top, Ok.Width, Ok.Height }, font = FontInfo(Ok.Font), clientSize = new { ClientSize.Width, ClientSize.Height } };

    protected override void ApplyLayout(int effectiveDpi) { }

    protected override Size ComputeClientSize(int eff) =>
        new(Native.MulDiv(LogicalClient.Width, eff, 96), Native.MulDiv(LogicalClient.Height, eff, 96));
}
