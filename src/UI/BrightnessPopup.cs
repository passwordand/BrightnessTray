using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BrightnessTray.Core;
using BrightnessTray.Interop;

namespace BrightnessTray.UI;

/// <summary>
/// 从托盘图标弹出的亮度调节面板。
/// 左键点击托盘图标时出现；点击面板外部或按 Esc 收起。
/// 每台显示器一行滑块，另有一条「全部屏幕」总控。
/// </summary>
internal sealed class BrightnessPopup : Form
{
    private readonly Action<string, int> _setOne;
    private readonly Action<int> _setAll;
    private readonly Action<bool> _restoreChanged;
    private readonly Action _exitRequested;
    private readonly Action _refreshRequested;

    private readonly Panel _scrollHost = new();
    private readonly TableLayoutPanel _table = new();
    private readonly Label _hint = new();
    private readonly CheckBox _chkRestore = new();

    private readonly List<MonitorRow> _rows = [];
    private MonitorRow? _master;
    private bool _suppressEvents;
    private DateTime _shownAt = DateTime.MinValue;

    /// <summary>鼠标钩子句柄，仅在面板可见期间安装。</summary>
    private IntPtr _mouseHook = IntPtr.Zero;

    /// <summary>保存委托引用，防止被 GC 回收导致钩子回调崩溃。</summary>
    private NativeMethods.LowLevelMouseProc? _mouseProc;

    /// <summary>点击托盘图标时由外部调用，用来区分「点托盘关闭」与「点外部关闭」。</summary>
    private Func<bool>? _isPointOnTrayIcon;

    /// <summary>设置「光标是否在托盘图标上」的判断回调（由托盘宿主提供）。</summary>
    public void SetTrayHitTest(Func<bool> hitTest) => _isPointOnTrayIcon = hitTest;

    public BrightnessPopup(
        Action<string, int> setOne,
        Action<int> setAll,
        Action<bool> restoreChanged,
        Action refreshRequested,
        Action exitRequested,
        bool restoreOnStartup)
    {
        _setOne = setOne;
        _setAll = setAll;
        _restoreChanged = restoreChanged;
        _refreshRequested = refreshRequested;
        _exitRequested = exitRequested;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(250, 250, 252);
        Padding = new Padding(1);
        DoubleBuffered = true;
        KeyPreview = true;

        BuildShell(restoreOnStartup);
    }

    /// <summary>圆角 + 细边框。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(210, 213, 220), 1);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = RoundedRect(rect, 8);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void BuildShell(bool restoreOnStartup)
    {
        // ── 标题栏 ──
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Color.FromArgb(250, 250, 252),
            Padding = new Padding(14, 0, 8, 0),
        };

        var title = new Label
        {
            Text = "屏幕亮度",
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(32, 34, 40),
            AutoSize = true,
            Location = new Point(14, 11),
        };

        var btnRefresh = MakeIconButton("刷新", "重新检测显示器");
        btnRefresh.Click += (_, _) => _refreshRequested();

        header.Controls.Add(title);
        header.Controls.Add(btnRefresh);
        header.Resize += (_, _) =>
        {
            header.Height = (int)Math.Ceiling(40 * DeviceDpi / 96.0);
            btnRefresh.Location = new Point(header.Width - btnRefresh.Width - 8, 8);
        };

        // ── 滑块区域 ──
        // 表格铺满可用宽度，列宽随窗口伸缩；AutoSize 关闭，避免出现横向滚动条
        _table.ColumnCount = 3;
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        _table.Dock = DockStyle.Fill;
        _table.AutoSize = false;
        _table.Padding = new Padding(4, 2, 4, 2);
        _table.Margin = new Padding(0);
        _table.BackColor = Color.FromArgb(250, 250, 252);

        _scrollHost.Dock = DockStyle.Fill;
        _scrollHost.AutoScroll = false;
        _scrollHost.BackColor = Color.FromArgb(250, 250, 252);
        _scrollHost.Controls.Add(_table);

        // ── 底部 ──
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Color.FromArgb(250, 250, 252),
            Padding = new Padding(14, 0, 10, 0),
        };
        footer.Resize += (_, _) => footer.Height = (int)Math.Ceiling(42 * DeviceDpi / 96.0);

        _chkRestore.Text = "开机恢复上次亮度";
        _chkRestore.Font = new Font("Microsoft YaHei UI", 9f);
        _chkRestore.ForeColor = Color.FromArgb(70, 74, 84);
        _chkRestore.AutoSize = true;
        _chkRestore.Checked = restoreOnStartup;
        _chkRestore.Location = new Point(12, 12);
        _chkRestore.CheckedChanged += (_, _) =>
        {
            if (!_suppressEvents)
            {
                _restoreChanged(_chkRestore.Checked);
            }
        };

        var btnExit = MakeIconButton("退出", "关闭程序");
        btnExit.Click += (_, _) => _exitRequested();
        btnExit.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        footer.Controls.Add(_chkRestore);
        footer.Controls.Add(btnExit);
        footer.Resize += (_, _) =>
        {
            // 按钮贴右侧，复选框贴左侧，避免相互覆盖
            btnExit.Location = new Point(footer.Width - btnExit.Width - 12, 9);
        };

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 0;
        _hint.Font = new Font("Microsoft YaHei UI", 8.5f);
        _hint.ForeColor = Color.FromArgb(150, 120, 40);
        _hint.Padding = new Padding(14, 2, 14, 4);
        _hint.Visible = false;

        Controls.Add(_scrollHost);
        Controls.Add(footer);
        Controls.Add(_hint);
        Controls.Add(header);
    }

    private static Button MakeIconButton(string text, string tooltip)
    {
        var btn = new Button
        {
            Text = text,
            Size = new Size(38, 24),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(80, 84, 94),
            BackColor = Color.FromArgb(240, 241, 245),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        btn.FlatAppearance.BorderSize = 0;
        new ToolTip().SetToolTip(btn, tooltip);
        return btn;
    }

    /// <summary>用最新的显示器列表重建所有滑块行。</summary>
    public void Rebuild(IReadOnlyList<MonitorInfo> monitors)
    {
        _suppressEvents = true;
        try
        {
            _table.SuspendLayout();
            _table.Controls.Clear();
            _table.RowStyles.Clear();
            _table.RowCount = 0;
            _rows.Clear();
            _master = null;

            var supported = monitors.Where(m => m.DdcSupported).ToList();

            // 「全部屏幕」总控（两台及以上才有意义）
            if (supported.Count >= 2)
            {
                int avg = (int)Math.Round(supported.Average(m => m.Percent));
                _master = AddRow("全部屏幕", avg, isMaster: true, enabled: true, tooltip: "同时调节所有显示器");
                AddSeparator();
            }

            foreach (MonitorInfo monitor in monitors)
            {
                string tip = monitor.DdcSupported
                    ? $"{monitor.DeviceName} · 当前 {monitor.Percent}%"
                    : monitor.Error ?? "该显示器不支持 DDC/CI 亮度控制";

                AddRow(
                    monitor.DisplayTitle,
                    monitor.Percent,
                    isMaster: false,
                    enabled: monitor.DdcSupported,
                    tooltip: tip,
                    key: monitor.Key);
            }

            if (monitors.Count == 0)
            {
                var empty = new Label
                {
                    Text = "未检测到显示器",
                    AutoSize = true,
                    Font = new Font("Microsoft YaHei UI", 9.5f),
                    ForeColor = Color.FromArgb(140, 144, 152),
                    Margin = new Padding(10, 14, 0, 14),
                };
                _table.RowCount = 1;
                _table.Controls.Add(empty, 0, 0);
                _table.SetColumnSpan(empty, 3);
            }

            // 有显示器不支持 DDC 时给出提示
            var failed = monitors.Where(m => !m.DdcSupported).ToList();
            if (failed.Count > 0)
            {
                _hint.Text = string.Join("；", failed.Select(m => $"{m.Name}：{m.Error}"));
                _hint.Visible = true;
                _hint.Height = 34;
            }
            else
            {
                _hint.Visible = false;
                _hint.Height = 0;
            }
        }
        finally
        {
            _table.ResumeLayout(true);
            _suppressEvents = false;
        }

        ApplySize(monitors.Count);
    }

    /// <summary>
    /// 按实际行数重算面板尺寸。行数取表格真实行数，而不是显示器台数，
    /// 因为「全部屏幕」总控和多屏分隔线也会占行。
    /// </summary>
    private void ApplySize(int monitorCount)
    {
        // 按当前 DPI 换算行高：100% 缩放下每行 44px，175% 则需要 77px
        float scale = DeviceDpi / 96f;
        int rowHeight = (int)Math.Ceiling(44 * scale);
        int headerHeight = (int)Math.Ceiling(40 * scale);
        int footerHeight = (int)Math.Ceiling(42 * scale);

        // 用表格实际行数，确保每一行都有位置，不会被裁掉
        int actualRows = Math.Max(1, _table.RowCount);
        int contentHeight = actualRows * rowHeight + 10;

        // 不依赖 Screen.FromControl：窗口尚未创建时它可能抛异常
        int screenHeight = 1080;
        try
        {
            screenHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 1080;
        }
        catch
        {
            // 用默认值
        }

        int maxBodyHeight = screenHeight - headerHeight - footerHeight - 40;
        int bodyHeight = Math.Min(contentHeight, Math.Max(rowHeight, maxBodyHeight));

        int hintHeight = _hint.Visible ? (int)Math.Ceiling(36 * scale) : 0;
        ClientSize = new Size((int)Math.Ceiling(430 * scale), headerHeight + bodyHeight + footerHeight + hintHeight);
    }

    private MonitorRow AddRow(string label, int percent, bool isMaster, bool enabled, string tooltip, string? key = null)
    {
        int rowIndex = _table.RowCount;
        _table.RowCount = rowIndex + 1;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, (float)Math.Ceiling(44 * DeviceDpi / 96.0)));

        var nameLabel = new Label
        {
            Text = label,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Microsoft YaHei UI", isMaster ? 9.5f : 9f, isMaster ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = enabled ? Color.FromArgb(40, 42, 48) : Color.FromArgb(170, 172, 178),
            Margin = new Padding(8, 0, 2, 0),
            AutoEllipsis = true,
        };

        var slider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Value = Math.Clamp(percent, 0, 100),
            Dock = DockStyle.Fill,
            Height = 32,
            Enabled = enabled,
            Margin = new Padding(0, 6, 0, 0),
        };

        var valueLabel = new Label
        {
            Text = percent + "%",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Consolas", 9.5f),
            ForeColor = enabled ? Color.FromArgb(60, 64, 72) : Color.FromArgb(170, 172, 178),
            Margin = new Padding(0, 0, 6, 0),
            UseMnemonic = false,
        };

        var row = new MonitorRow(key, nameLabel, slider, valueLabel);

        slider.ValueChanged += (_, _) =>
        {
            if (_suppressEvents)
            {
                return;
            }

            int value = slider.Value;
            valueLabel.Text = value + "%";

            if (isMaster)
            {
                _setAll(value);

                // 总控拖动时同步各分屏显示
                _suppressEvents = true;
                foreach (MonitorRow r in _rows)
                {
                    r.Slider.Value = value;
                    r.Value.Text = value + "%";
                }
                _suppressEvents = false;
            }
            else
            {
                _setOne(row.Key!, value);
            }
        };

        var tip = new ToolTip();
        tip.SetToolTip(nameLabel, tooltip);
        tip.SetToolTip(slider, tooltip);

        _table.Controls.Add(nameLabel, 0, rowIndex);
        _table.Controls.Add(slider, 1, rowIndex);
        _table.Controls.Add(valueLabel, 2, rowIndex);

        _rows.Add(row);
        return row;
    }

    private void AddSeparator()
    {
        int rowIndex = _table.RowCount;
        _table.RowCount = rowIndex + 1;
        _table.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));

        var line = new Panel
        {
            Height = 1,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(228, 230, 236),
            Margin = new Padding(10, 3, 10, 4),
        };

        _table.Controls.Add(line, 0, rowIndex);
        _table.SetColumnSpan(line, 3);
    }

    /// <summary>外部（引擎回报）更新单台显示器的显示值，不触发写硬件。</summary>
    public void UpdateValue(string key, int percent)
    {
        MonitorRow? row = _rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        _suppressEvents = true;
        row.Slider.Value = Math.Clamp(percent, 0, 100);
        row.Value.Text = percent + "%";
        _suppressEvents = false;
    }

    public void UpdateRestoreFlag(bool value)
    {
        _suppressEvents = true;
        _chkRestore.Checked = value;
        _suppressEvents = false;
    }

    /// <summary>在指定位置弹出（自动避开屏幕边缘）。</summary>
    public void ShowAt(Point anchor)
    {
        _shownAt = DateTime.UtcNow;

        // 布局与 DPI 此时才完全就绪，重新校正一次尺寸，避免行被裁掉
        ApplySize(_rows.Count);

        Rectangle work = GetWorkingArea(anchor);

        int x = anchor.X - ClientSize.Width / 2;
        x = Math.Min(x, work.Right - ClientSize.Width - 8);
        x = Math.Max(work.Left + 8, x);

        // 优先显示在鼠标上方；放不下就翻到下方，最后兜底夹紧在工作区内，
        // 避免面板压到任务栏或被屏幕边缘裁掉
        int y = anchor.Y - ClientSize.Height - 8;
        if (y < work.Top + 8)
        {
            y = anchor.Y + 8;
        }

        y = Math.Min(y, work.Bottom - ClientSize.Height - 8);
        y = Math.Max(work.Top + 8, y);

        Location = new Point(x, y);

        Show();
        Activate();
        BringToFront();
        InstallMouseHook();
    }

    /// <summary>
    /// 取包含指定点的屏幕工作区。优先用 Screen API，
    /// 异常时退回主屏，保证弹出流程不会中断。
    /// </summary>
    private static Rectangle GetWorkingArea(Point point)
    {
        try
        {
            Screen? screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(point));
            return (screen ?? Screen.PrimaryScreen)?.WorkingArea
                ?? new Rectangle(0, 0, 1920, 1080);
        }
        catch
        {
            return new Rectangle(0, 0, 1920, 1080);
        }
    }

    /// <summary>面板显示时安装全局鼠标钩子，用于检测「点击面板外部」。</summary>
    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        _mouseProc = OnLowLevelMouse;
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
    }

    /// <summary>面板隐藏时卸载钩子，避免长期占用。</summary>
    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    /// <summary>
    /// 低层鼠标钩子：检测到「左键按下」且位置在面板外时收起面板。
    /// 点在托盘图标上时不收起，交给托盘点击逻辑做「再点一次关闭」的切换。
    /// </summary>
    private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
        {
            NativeMethods.MSLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var point = new Point(info.pt.x, info.pt.y);

            if (!Bounds.Contains(point) && (_isPointOnTrayIcon is null || !_isPointOnTrayIcon()))
            {
                // 钩子回调里不能直接操作窗口，切回 UI 线程再隐藏
                BeginInvoke(Hide);
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (!Visible)
        {
            UninstallMouseHook();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        // 不做自动隐藏：拖动滑块、点击系统音量等都会触发 Deactivate，
        // 靠它收起面板容易误关闭。关闭改由全局鼠标钩子判断「点到面板外面」。
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>防止用户用 Alt+F4 之类关闭，只隐藏。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        UninstallMouseHook();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UninstallMouseHook();
        }

        base.Dispose(disposing);
    }

    private sealed record MonitorRow(string? Key, Label NameLabel, TrackBar Slider, Label Value);
}
