using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace AppleCinemaBrightness;

internal sealed class MainForm : Form
{
    private readonly FlowLayoutPanel list = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, AutoScroll = true, Padding = new Padding(8)
    };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 40,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 8, 0) };
    private readonly NotifyIcon tray;
    private readonly List<DisplayRow> rows = new();
    private bool exiting;

    public MainForm()
    {
        Text = "Apple Cinema Display 輝度調整";
        Icon = SystemIcons.Application;
        Width = 585;
        Height = 380;
        MinimumSize = new Size(490, 260);
        StartPosition = FormStartPosition.CenterScreen;

        Button refresh = new() { Text = "ディスプレイを再検出", Width = 180,
            Height = 32, Location = new Point(12, 10) };
        refresh.Click += (_, _) => RefreshDisplays();
        Label help = new() { Text = "Ctrl + Alt + ↑ / ↓ で最初のディスプレイを調整",
            AutoSize = true, Location = new Point(207, 18) };
        Panel top = new() { Dock = DockStyle.Top, Height = 53 };
        top.Controls.Add(refresh);
        top.Controls.Add(help);
        Controls.Add(list);
        Controls.Add(top);
        Controls.Add(status);

        var menu = new ContextMenuStrip();
        menu.Items.Add("表示", null, (_, _) => RestoreWindow());
        menu.Items.Add("ディスプレイ再検出", null, (_, _) => RefreshDisplays());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Text = "Apple Cinema Display 輝度調整",
            Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => RestoreWindow();

        Shown += (_, _) => RefreshDisplays();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // MOD_CONTROL (0x2) | MOD_ALT (0x1)
        Native.RegisterHotKey(Handle, 1001, 0x0003, (uint)Keys.Up);
        Native.RegisterHotKey(Handle, 1002, 0x0003, (uint)Keys.Down);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, 1001);
        Native.UnregisterHotKey(Handle, 1002);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && rows.Count > 0) // WM_HOTKEY
        {
            if (m.WParam.ToInt64() == 1001) rows[0].Adjust(5);
            else if (m.WParam.ToInt64() == 1002) rows[0].Adjust(-5);
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        foreach (DisplayRow row in rows) row.Dispose();
        rows.Clear();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RefreshDisplays()
    {
        foreach (DisplayRow row in rows) row.Dispose();
        rows.Clear();
        list.Controls.Clear();
        try
        {
            List<HidDisplay> detected = HidDisplay.Discover();
            foreach (HidDisplay display in detected)
            {
                try
                {
                    DisplayRow row = new(display, ShowStatus);
                    rows.Add(row);
                    list.Controls.Add(row.Group);
                }
                catch (Exception ex)
                {
                    display.Dispose();
                    ShowStatus($"{display.Name}: {ex.Message}");
                }
            }
            status.Text = detected.Count == 0
                ? "対応ディスプレイを検出できません。USB接続と対応モデルを確認してください。"
                : $"検出: {rows.Count} 台。スライダー操作でバックライトを変更します。";
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
    }

    private void ShowStatus(string message) => status.Text = message;
}

internal sealed class DisplayRow : IDisposable
{
    private readonly HidDisplay device;
    private readonly Action<string> status;
    private readonly TrackBar slider;
    private readonly Label valueLabel;
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 160 };
    private readonly CheckBox? autoBox;

    public GroupBox Group { get; }

    public DisplayRow(HidDisplay display, Action<string> showStatus)
    {
        device = display;
        status = showStatus;
        Group = new GroupBox { Text = $"{display.Name} (PID {display.ProductId:X4})",
            Width = 518, Height = 145 };

        int percent;
        try { percent = device.ReadBrightnessPercent(); }
        catch (Exception ex) { throw new IOException("初期輝度を取得できません: " + ex.Message, ex); }

        slider = new TrackBar { Minimum = 1, Maximum = 100, TickFrequency = 10,
            LargeChange = 10, SmallChange = 1,
            Value = Math.Clamp(percent, 1, 100), Left = 12, Top = 30, Width = 405 };
        valueLabel = new Label { Text = $"{percent}%", Left = 424, Top = 45,
            Width = 70, TextAlign = ContentAlignment.MiddleLeft };
        Group.Controls.Add(slider);
        Group.Controls.Add(valueLabel);

        if (display.SupportsAutoBrightness)
        {
            autoBox = new CheckBox { Text = "自動輝度調整", Left = 16, Top = 100,
                Width = 160 };
            try { autoBox.Checked = device.ReadAutoBrightness(); }
            catch { autoBox.Checked = false; }
            slider.Enabled = !autoBox.Checked;
            autoBox.CheckedChanged += (_, _) =>
            {
                try
                {
                    device.SetAutoBrightness(autoBox.Checked);
                    slider.Enabled = !autoBox.Checked;
                }
                catch (Exception ex) { status($"自動輝度の変更に失敗: {ex.Message}"); }
            };
            Group.Controls.Add(autoBox);
        }

        var maxButton = new Button { Text = "100%", Left = 420, Top = 94,
            Width = 70, Height = 27 };
        maxButton.Click += (_, _) => { if (autoBox?.Checked == true) autoBox.Checked = false; Adjust(100 - slider.Value); };
        Group.Controls.Add(maxButton);

        slider.ValueChanged += (_, _) =>
        {
            valueLabel.Text = $"{slider.Value}%";
            debounce.Stop();
            debounce.Start();
        };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            try { device.SetBrightnessPercent(slider.Value); }
            catch (Exception ex) { status($"輝度変更に失敗: {ex.Message}"); }
        };
    }

    public void Adjust(int delta) => slider.Value = Math.Clamp(slider.Value + delta, 1, 100);

    public void Dispose()
    {
        debounce.Stop();
        debounce.Dispose();
        Group.Dispose();
        device.Dispose();
    }
}
