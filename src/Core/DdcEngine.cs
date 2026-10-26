using System.Runtime.InteropServices;
using BrightnessTray.Interop;

namespace BrightnessTray.Core;

/// <summary>
/// DDC/CI 引擎：在一条独立的后台线程上枚举显示器并读写硬件亮度。
/// <para>
/// 之所以独占一个线程：<c>SetVCPFeature</c> / <c>GetVCPFeatureAndVCPFeatureReply</c>
/// 走的是 I2C 通道，单次耗时几十毫秒甚至更长，放到 UI 线程会明显卡顿。
/// 拖动滑块时新值会覆盖旧值（只保留最新目标值），因此不会堆积请求。
/// </para>
/// </summary>
public sealed class DdcEngine : IDisposable
{
    private readonly object _sync = new();

    /// <summary>诊断日志，写入 %APPDATA%\BrightnessTray\trace.log。</summary>
    internal static void Trace(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrightnessTray");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "trace.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [ddc] {message}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>key → 物理显示器句柄。仅在后台线程访问。</summary>
    private readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>key → 可写状态。仅在后台线程访问。</summary>
    private readonly Dictionary<string, MonitorState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GDI 设备名 → NVIDIA 独立 DDC 通道。仅后台线程使用。</summary>
    private readonly Dictionary<string, NvApiDdc.Channel> _nvChannels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>待写入的目标百分比，后写入的值覆盖先前的值。</summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly AutoResetEvent _evRefresh = new(false);
    private readonly AutoResetEvent _evApply = new(false);
    private readonly AutoResetEvent _evStop = new(false);

    private Thread? _thread;
    private volatile bool _alive;
    private bool _disposed;

    /// <summary>枚举完成（成功或失败）后触发，参数为显示器快照列表。</summary>
    public event Action<IReadOnlyList<MonitorInfo>>? MonitorsChanged;

    /// <summary>亮度写入完成后触发。</summary>
    public event Action<string, int, bool, string?>? BrightnessChanged;

    /// <summary>启动后台线程。</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _alive = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "BrightnessTray.DDC",
        };
        _thread.Start();
    }

    /// <summary>请求重新枚举显示器（插拔、分辨率变化后调用）。</summary>
    public void RequestRefresh()
    {
        if (!_disposed)
        {
            _evRefresh.Set();
        }
    }

    /// <summary>请求把某台显示器设置为指定百分比（0-100）。</summary>
    public void RequestSet(string key, int percent)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _pending[key] = Math.Clamp(percent, 0, 100);
        }

        _evApply.Set();
    }

    private void Loop()
    {
        WaitHandle[] handles = [_evStop, _evRefresh, _evApply];

        while (_alive)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles, 250);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            switch (index)
            {
                case 0:
                    return;
                case 1:
                    SafeRefresh();
                    break;
                case 2:
                    SafeApply();
                    break;
                default:
                    // 超时：什么都不做，继续等待
                    break;
            }
        }
    }

    private void SafeRefresh()
    {
        try
        {
            RefreshCore();
        }
        catch (Exception ex)
        {
            MonitorsChanged?.Invoke([]);
            BrightnessChanged?.Invoke(string.Empty, 0, false, "枚举显示器失败：" + ex.Message);
        }
    }

    private void SafeApply()
    {
        try
        {
            ApplyCore();
        }
        catch (Exception ex)
        {
            BrightnessChanged?.Invoke(string.Empty, 0, false, "设置亮度失败：" + ex.Message);
        }
    }

    /// <summary>枚举过程中的单台显示器原始数据。</summary>
    private sealed record FoundMonitor(
        string Key,
        string Name,
        string DeviceName,
        bool IsPrimary,
        bool Ok,
        int Percent,
        uint Max,
        IntPtr Handle,
        string? Error,
        NvApiDdc.Channel? NvChannel);

    /// <summary>重新枚举所有显示器并读取当前亮度。</summary>
    private void RefreshCore()
    {
        var found = new List<FoundMonitor>();

        // NVAPI 能按 displayMask 精确区分两块 NVIDIA 显示器，优先建立映射。
        _nvChannels.Clear();
        foreach (var kv in NvApiDdc.CreateChannelMap())
        {
            _nvChannels[kv.Key] = kv.Value;
        }

        Trace($"NVAPI 通道数={_nvChannels.Count}");

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            CollectMonitor(hMonitor, found);
            return true;
        }, IntPtr.Zero);

        // 先建好新句柄再替换旧的，避免「刷新瞬间写入失败」的竞态：
        // 之前先 ReleaseHandles() 再重建，中间的窗口期会让拖动滑块报「显示器已断开」
        var newHandles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        var newStates = new Dictionary<string, MonitorState>(StringComparer.OrdinalIgnoreCase);

        var snapshot = new List<MonitorInfo>(found.Count);

        foreach (var item in found)
        {
            // 注意：部分显卡驱动（如本机 NVIDIA + Intel 混合输出）返回的
            // hPhysicalMonitor 可能为 0，但 VCP 调用依然有效，因此不能以句柄是否为 0
            // 作为「是否可用」的判据，只以 DDC 读取结果 Ok 为准。
            if (item.Ok)
            {
                newHandles[item.Key] = item.Handle;
                newStates[item.Key] = new MonitorState(item.Key, item.Max, item.NvChannel);
            }

            snapshot.Add(new MonitorInfo
            {
                Key = item.Key,
                Name = item.Name,
                DeviceName = item.DeviceName,
                IsPrimary = item.IsPrimary,
                Percent = item.Percent,
                DdcSupported = item.Ok,
                Error = item.Error,
            });
        }

        // 新句柄已就绪，原子替换；再释放旧句柄
        Dictionary<string, IntPtr> oldHandles;
        lock (_sync)
        {
            oldHandles = new Dictionary<string, IntPtr>(_handles, StringComparer.OrdinalIgnoreCase);

            _handles.Clear();
            foreach (var kv in newHandles)
            {
                _handles[kv.Key] = kv.Value;
            }

            _states.Clear();
            foreach (var kv in newStates)
            {
                _states[kv.Key] = kv.Value;
            }
        }

        foreach (IntPtr handle in oldHandles.Values)
        {
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            // 新表里复用了同一个句柄就不要释放
            if (newHandles.ContainsValue(handle))
            {
                continue;
            }

            try
            {
                NativeMethods.DestroyPhysicalMonitor(handle);
            }
            catch
            {
                // 忽略释放失败
            }
        }

        MonitorsChanged?.Invoke(snapshot);
    }

    /// <summary>枚举单个 HMONITOR 下的物理显示器。</summary>
    private void CollectMonitor(IntPtr hMonitor, List<FoundMonitor> sink)
    {
        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
        };

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return;
        }

        // 设备接口路径 → 用于读取 EDID 型号名，同时作为稳定 key
        var device = new NativeMethods.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
        };

        string? iface = null;
        if (NativeMethods.EnumDisplayDevices(info.szDevice, 0, ref device, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
        {
            iface = device.DeviceID;
        }

        string name = EdidReader.GetMonitorName(iface) ?? info.szDevice;
        bool isPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;

        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
        {
            sink.Add(new FoundMonitor(
                iface ?? info.szDevice, name, info.szDevice, isPrimary,
                Ok: false, Percent: 0, Max: 100, Handle: IntPtr.Zero,
                Error: "系统未返回物理显示器", NvChannel: null));
            return;
        }

        var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
        {
            sink.Add(new FoundMonitor(
                iface ?? info.szDevice, name, info.szDevice, isPrimary,
                Ok: false, Percent: 0, Max: 100, Handle: IntPtr.Zero,
                Error: "无法打开显示器控制通道", NvChannel: null));
            return;
        }

        for (int i = 0; i < physicals.Length; i++)
        {
            IntPtr handle = physicals[i].hPhysicalMonitor;
            string key = count > 1 ? $"{iface ?? info.szDevice}#p{i}" : (iface ?? info.szDevice);

            // 优先使用 NVIDIA 原生通道；DXVA2 只在没有 NVAPI 的机器上回退。
            bool ok;
            int percent;
            uint max;
            string? error;
            NvApiDdc.Channel? nvChannel = null;

            if (_nvChannels.TryGetValue(info.szDevice, out NvApiDdc.Channel channel))
            {
                nvChannel = channel;
                ok = NvApiDdc.TryGetBrightness(channel, out percent, out max, out error);
            }
            else
            {
                ok = TryGetBrightness(handle, out percent, out max, out error);
            }

            sink.Add(new FoundMonitor(
                key, name, info.szDevice, isPrimary,
                ok, percent, max == 0 ? 100 : max, handle, error, nvChannel));
        }
    }

    /// <summary>
    /// 读取 VCP 0x10 当前值。显示器刚唤醒、锁屏或刚从睡眠恢复时，
    /// DDC 通道会短暂不可用，所以这里做多次退避重试。
    /// </summary>
    private static bool TryGetBrightness(IntPtr handle, out int percent, out uint max, out string? error)
    {
        percent = 0;
        max = 100;
        error = null;

        // 重试间隔递增：60/120/240/480ms，总等待约 0.9 秒，兼顾唤醒慢的显示器
        int delay = 60;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, NativeMethods.VCP_BRIGHTNESS, out _, out uint current, out uint maximum))
            {
                max = maximum == 0 ? 100u : maximum;
                percent = (int)Math.Round(current * 100.0 / max);
                percent = Math.Clamp(percent, 0, 100);
                return true;
            }

            if (attempt < 3)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }

        error = "显示器未响应 DDC/CI（锁屏或休眠唤醒后可能短暂失效，请点「刷新」重试）";
        return false;
    }

    /// <summary>把待写入的目标值刷到硬件上。</summary>
    private void ApplyCore()
    {
        List<KeyValuePair<string, int>> work;
        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            work = [.. _pending];
            _pending.Clear();
        }

        // 还没有句柄表时先同步枚举一次，否则写入必然失败
        // （RequestSet 可能先于首次 RequestRefresh 到达）
        if (_handles.Count == 0)
        {
            try
            {
                RefreshCore();
            }
            catch
            {
                // 枚举失败时下面的循环会逐项报告失败
            }
        }

        foreach ((string key, int percent) in work)
        {
            if (!_states.TryGetValue(key, out MonitorState? state))
            {
                // 句柄表可能刚被刷新替换过，重新枚举一次再试，避免误报「已断开」
                try
                {
                    RefreshCore();
                }
                catch
                {
                    // 交给下面的失败分支处理
                }

                if (!_states.TryGetValue(key, out state))
                {
                    BrightnessChanged?.Invoke(key, percent, false, "显示器已断开");
                    continue;
                }
            }

            if (!_handles.TryGetValue(key, out IntPtr handle))
            {
                BrightnessChanged?.Invoke(key, percent, false, "无法控制该显示器");
                continue;
            }

            uint raw = (uint)Math.Round(Math.Clamp(percent, 0, 100) * state.Max / 100.0);
            bool ok = false;
            string? writeError = null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (state.NvChannel is { } nvChannel)
                {
                    ok = NvApiDdc.SetBrightness(nvChannel, raw, out writeError);
                }
                else
                {
                    ok = NativeMethods.SetVCPFeature(handle, NativeMethods.VCP_BRIGHTNESS, raw);
                    writeError = ok ? null : "DXVA2 写入失败";
                }

                if (ok)
                {
                    break;
                }

                Thread.Sleep(40);
            }

            if (ok)
            {
                state.Percent = percent;
            }

            BrightnessChanged?.Invoke(key, percent, ok, ok ? null : (writeError ?? "写入亮度失败"));
        }
    }

    private void ReleaseHandles()
    {
        foreach (IntPtr handle in _handles.Values)
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.DestroyPhysicalMonitor(handle);
                }
                catch
                {
                    // 忽略释放失败
                }
            }
        }

        _handles.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _alive = false;
        _evStop.Set();

        try
        {
            _thread?.Join(1500);
        }
        catch
        {
            // 忽略
        }

        ReleaseHandles();
        NvApiDdc.Shutdown();
        _evRefresh.Dispose();
        _evApply.Dispose();
        _evStop.Dispose();
    }

    /// <summary>单台显示器的可变状态（仅后台线程访问）。</summary>
    private sealed class MonitorState(string key, uint max, NvApiDdc.Channel? nvChannel)
    {
        public string Key { get; } = key;

        public uint Max { get; } = max == 0 ? 100u : max;

        public NvApiDdc.Channel? NvChannel { get; } = nvChannel;

        public int Percent { get; set; }
    }
}
