using BrightnessTray.Core;
using BrightnessTray.Interop;
using BrightnessTray.UI;
using Microsoft.Win32;

namespace BrightnessTray;

/// <summary>
/// 托盘应用主逻辑：托盘图标、右键菜单、弹出面板、显示器热插拔监听。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string AppName = "BrightnessTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly DdcEngine _engine = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly BrightnessPopup _popup;
    private readonly DisplayChangeWatcher _watcher = new();

    private IReadOnlyList<MonitorInfo> _monitors = [];
    private Icon? _currentIcon;
    private bool _restorePending;

    public TrayApplicationContext()
    {
        _popup = new BrightnessPopup(
            setOne: (key, percent) => _engine.RequestSet(key, percent),
            setAll: SetAll,
            restoreChanged: OnRestoreChanged,
            refreshRequested: () => _engine.RequestRefresh(),
            exitRequested: ExitApplication,
            restoreOnStartup: _settings.RestoreOnStartup);

        // 点击托盘图标区域时，由托盘自身的点击处理负责切换，避免钩子重复关闭
        _popup.SetTrayHitTest(IsCursorOnTrayIcon);

        _tray = new NotifyIcon
        {
            Text = "屏幕亮度",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // 先创建图标再赋给托盘，避免在对象初始化器里访问尚未赋值的 _tray
        UpdateIcon(50);

        _tray.MouseUp += OnTrayMouseUp;

        _engine.MonitorsChanged += OnMonitorsChanged;
        _engine.BrightnessChanged += OnBrightnessChanged;

        _watcher.DisplayChanged += OnDisplayChanged;

        // 锁屏/休眠会暂时掐断 DDC 通道，解锁或唤醒后必须重新枚举才能恢复控制
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        BuildMenu();
        _engine.Start();
        _engine.RequestRefresh();
    }

    /// <summary>解锁回到桌面时重新检测显示器。</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            _engine.RequestRefresh();
        }
    }

    /// <summary>从睡眠恢复时重新检测显示器。</summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _engine.RequestRefresh();
        }
    }

    /// <summary>统一处理托盘图标的鼠标点击。</summary>
    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            TogglePopup();
        }
    }

    private void TogglePopup()
    {
        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }

        _popup.Rebuild(_monitors);
        _popup.ShowAt(Cursor.Position);
    }

    /// <summary>
    /// 判断光标是否落在任务栏托盘区域。用于区分「点托盘图标」和「点面板外部」，
    /// 否则点击托盘会被鼠标钩子先关掉、再被托盘点击重新打开，造成闪烁。
    /// </summary>
    private static bool IsCursorOnTrayIcon()
    {
        Point cursor = Cursor.Position;

        foreach (string cls in (string[])["Shell_TrayWnd", "Shell_SecondaryTrayWnd"])
        {
            IntPtr hwnd = NativeMethods.FindWindow(cls, null);
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            {
                continue;
            }

            if (cursor.X >= rect.Left && cursor.X <= rect.Right && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    // ── 引擎回调 ──

    private void OnMonitorsChanged(IReadOnlyList<MonitorInfo> monitors)
    {
        if (_popup.IsHandleCreated)
        {
            _popup.BeginInvoke(() => ApplyMonitors(monitors));
        }
        else
        {
            ApplyMonitors(monitors);
        }
    }

    private void ApplyMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        _monitors = monitors;

        if (monitors.Count == 0)
        {
            _tray.Text = "屏幕亮度：未检测到显示器";
        }
        else
        {
            int avg = (int)Math.Round(monitors.Where(m => m.DdcSupported).Select(m => m.Percent).DefaultIfEmpty(50).Average());
            UpdateIcon(avg);

            string summary = string.Join(" · ", monitors.Select(m => $"{m.Name} {m.Percent}%"));
            // NotifyIcon.Text 上限 63 字符
            _tray.Text = summary.Length <= 62 ? "屏幕亮度：" + summary : "屏幕亮度";
        }

        BuildMenu();
        _popup.Rebuild(monitors);

        // 首次枚举完成后，按设置恢复上次亮度
        if (_restorePending)
        {
            _restorePending = false;
            RestoreLastBrightness(monitors);
        }
    }

    private void OnBrightnessChanged(string key, int percent, bool ok, string? error)
    {
        if (!ok)
        {
            return;
        }

        if (_popup.IsHandleCreated)
        {
            _popup.BeginInvoke(() => _popup.UpdateValue(key, percent));
        }

        // 记录最后亮度，供下次启动恢复
        if (key.Length > 0)
        {
            _settings.LastBrightness[key] = percent;
            _settings.Save();
        }
    }

    private void OnDisplayChanged()
    {
        // 显示器插拔或排列变化后重新枚举
        _engine.RequestRefresh();
    }

    // ── 菜单 ──

    private void BuildMenu()
    {
        _menu.Items.Clear();

        var header = new ToolStripMenuItem("屏幕亮度") { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        if (_monitors.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("未检测到显示器") { Enabled = false });
        }
        else
        {
            // 全部屏幕快捷档位
            var allItem = new ToolStripMenuItem("全部屏幕");
            foreach (int preset in new[] { 100, 75, 50, 25, 0 })
            {
                var presetItem = new ToolStripMenuItem(preset + "%");
                presetItem.Click += (_, _) => SetAll(preset);
                allItem.DropDownItems.Add(presetItem);
            }
            allItem.DropDownItems.Add(new ToolStripSeparator());

            foreach (int delta in new[] { 5, -5 })
            {
                var stepItem = new ToolStripMenuItem(delta > 0 ? "调亮 5%" : "调暗 5%");
                int d = delta;
                stepItem.Click += (_, _) => StepAll(d);
                allItem.DropDownItems.Add(stepItem);
            }

            _menu.Items.Add(allItem);
            _menu.Items.Add(new ToolStripSeparator());

            // 单台显示器
            foreach (MonitorInfo monitor in _monitors)
            {
                var item = new ToolStripMenuItem($"{monitor.DisplayTitle}  {monitor.Percent}%");

                if (!monitor.DdcSupported)
                {
                    item.Enabled = false;
                    item.ToolTipText = monitor.Error;
                    _menu.Items.Add(item);
                    continue;
                }

                foreach (int preset in new[] { 100, 80, 60, 40, 20 })
                {
                    var presetItem = new ToolStripMenuItem(preset + "%");
                    string key = monitor.Key;
                    presetItem.Click += (_, _) => _engine.RequestSet(key, preset);
                    item.DropDownItems.Add(presetItem);
                }

                _menu.Items.Add(item);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        var refreshItem = new ToolStripMenuItem("重新检测显示器");
        refreshItem.Click += (_, _) => _engine.RequestRefresh();
        _menu.Items.Add(refreshItem);

        var startupItem = new ToolStripMenuItem("开机自动启动")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true,
        };
        startupItem.Click += (_, _) => SetStartup(startupItem.Checked);
        _menu.Items.Add(startupItem);

        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();
        _menu.Items.Add(exitItem);
    }

    // ── 亮度操作 ──

    private void SetAll(int percent)
    {
        foreach (MonitorInfo monitor in _monitors.Where(m => m.DdcSupported))
        {
            _engine.RequestSet(monitor.Key, percent);
        }
    }

    private void StepAll(int delta)
    {
        foreach (MonitorInfo monitor in _monitors.Where(m => m.DdcSupported))
        {
            int target = Math.Clamp(monitor.Percent + delta, 0, 100);
            _engine.RequestSet(monitor.Key, target);
        }
    }

    private void RestoreLastBrightness(IReadOnlyList<MonitorInfo> monitors)
    {
        if (!_settings.RestoreOnStartup)
        {
            return;
        }

        foreach (MonitorInfo monitor in monitors.Where(m => m.DdcSupported))
        {
            if (_settings.LastBrightness.TryGetValue(monitor.Key, out int last) && last != monitor.Percent)
            {
                _engine.RequestSet(monitor.Key, last);
            }
        }
    }

    private void OnRestoreChanged(bool enabled)
    {
        _settings.RestoreOnStartup = enabled;
        _settings.Save();
    }

    // ── 图标 ──

    private Icon UpdateIcon(int percent)
    {
        Icon icon = TrayIconFactory.Create(percent);
        _tray.Icon = icon;

        Icon? previous = _currentIcon;
        _currentIcon = icon;
        previous?.Dispose();

        return icon;
    }

    // ── 开机启动 ──

    private static bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(AppName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "设置开机启动失败：" + ex.Message,
                "屏幕亮度",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    // ── 退出 ──

    private void ExitApplication()
    {
        _tray.Visible = false;

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _watcher.Dispose();
        _engine.MonitorsChanged -= OnMonitorsChanged;
        _engine.BrightnessChanged -= OnBrightnessChanged;
        _engine.Dispose();

        _settings.Save();

        _popup.Dispose();
        _menu.Dispose();
        _tray.Dispose();
        _currentIcon?.Dispose();

        ExitThread();
    }
}

/// <summary>
/// 监听显示配置变化（显示器插拔、分辨率/排列改变），触发重新枚举。
/// 通过隐藏的原生窗口接收 WM_DISPLAYCHANGE / WM_DEVICECHANGE。
/// </summary>
internal sealed class DisplayChangeWatcher : NativeWindow, IDisposable
{
    private bool _disposed;

    public DisplayChangeWatcher()
    {
        CreateHandle(new CreateParams
        {
            Caption = "BrightnessTray.DisplayWatcher",
            Parent = new IntPtr(-3), // HWND_MESSAGE：仅消息窗口
        });
    }

    /// <summary>显示配置发生变化。</summary>
    public event Action? DisplayChanged;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DEVICECHANGE)
        {
            // 合并短时间内的重复通知，避免频繁枚举
            DisplayChanged?.Invoke();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyHandle();
    }
}
