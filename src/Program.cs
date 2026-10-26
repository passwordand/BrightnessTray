using System.Text;

namespace BrightnessTray;

internal static class Program
{
    /// <summary>防止同时运行两个实例，避免争抢 DDC/CI 通道。</summary>
    private const string MutexName = @"Local\BrightnessTray.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        // 自检模式：BrightnessTray.exe --selftest [亮度] [显示器关键词]
        // 例如：--selftest 40 U28G2G6B（只测主屏）；省略关键词则测所有屏幕。
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            RunSelfTest(
                args.Length > 1 && int.TryParse(args[1], out int p) ? p : 50,
                args.Length > 2 ? args[2] : null);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "屏幕亮度托盘已经在运行了，请看任务栏右下角的托盘图标。",
                "屏幕亮度",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("未处理异常: " + e.ExceptionObject);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log("界面线程异常: " + e.Exception);
            ShowFatal(e.Exception);
        };

        try
        {
            using var context = new TrayApplicationContext();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            Log("启动失败: " + ex);
            ShowFatal(ex);
        }
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            "程序遇到未处理的错误：" + Environment.NewLine + ex.Message,
            "屏幕亮度",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    /// <summary>
    /// 自检：枚举显示器 → 读取当前亮度 → 写入目标亮度 → 回读确认 → 恢复原值。
    /// 结果同时打印到控制台并写入 trace.log。
    /// </summary>
    private static void RunSelfTest(int targetPercent, string? monitorFilter)
    {
        targetPercent = Math.Clamp(targetPercent, 0, 100);

        // WinExe 没有自带控制台，尝试附着到调用方控制台以便直接看到输出
        AttachConsole(-1);

        string reportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrightnessTray",
            "selftest.txt");
        var lines = new List<string>();

        void Emit(string s)
        {
            lines.Add(s);
            Console.WriteLine(s);
            Core.DdcEngine.Trace("[selftest] " + s);
        }

        Emit($"自检开始，目标亮度 {targetPercent}%，显示器筛选：{monitorFilter ?? "全部"}");

        var engine = new Core.DdcEngine();
        var monitors = new List<Core.MonitorInfo>();
        using var done = new ManualResetEventSlim(false);

        engine.MonitorsChanged += list =>
        {
            monitors.Clear();
            monitors.AddRange(list);
            done.Set();
        };

        engine.Start();
        engine.RequestRefresh();

        if (!done.Wait(TimeSpan.FromSeconds(15)))
        {
            Emit("枚举超时");
            engine.Dispose();
            return;
        }

        Emit($"检测到 {monitors.Count} 台显示器");
        foreach (Core.MonitorInfo m in monitors)
        {
            Emit($"  {m.Name} key={m.Key} 当前={m.Percent}% 支持={m.DdcSupported} err={m.Error}");
        }

        var results = new List<(string Key, int Percent, bool Ok, string? Error)>();
        engine.BrightnessChanged += (key, percent, ok, err) => results.Add((key, percent, ok, err));

        // 记住写入前的原亮度，自检结束后恢复
        var original = monitors
            .Where(x => x.DdcSupported)
            .ToDictionary(x => x.Key, x => x.Percent, StringComparer.OrdinalIgnoreCase);

        var targets = monitors
            .Where(x => x.DdcSupported)
            .Where(x => string.IsNullOrWhiteSpace(monitorFilter)
                || x.Name.Contains(monitorFilter, StringComparison.OrdinalIgnoreCase)
                || x.DeviceName.Contains(monitorFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targets.Count == 0)
        {
            Emit("没有匹配的显示器，未修改任何亮度。");
            engine.Dispose();
            return;
        }

        foreach (Core.MonitorInfo m in targets)
        {
            Emit($"写入 {m.Name} -> {targetPercent}%");
            engine.RequestSet(m.Key, targetPercent);
        }

        Thread.Sleep(2500);

        foreach (var r in results)
        {
            Emit($"  写入结果 key={r.Key} percent={r.Percent} ok={r.Ok} err={r.Error}");
        }

        // 回读验证
        Emit("重新枚举以回读实际亮度...");
        monitors.Clear();
        done.Reset();
        engine.RequestRefresh();
        if (done.Wait(TimeSpan.FromSeconds(15)))
        {
            foreach (Core.MonitorInfo m in monitors)
            {
                string verdict = targets.Any(t => t.Key.Equals(m.Key, StringComparison.OrdinalIgnoreCase))
                    ? (m.Percent == targetPercent ? "✔ 已生效" : "✘ 未生效")
                    : "（未修改）";
                Emit($"  回读 {m.Name} = {m.Percent}%  {verdict}");
            }
        }
        else
        {
            Emit("回读超时");
        }

        // 恢复原亮度（只恢复本次实际改过的屏幕）
        Emit("恢复原亮度...");
        foreach (Core.MonitorInfo m in targets)
        {
            engine.RequestSet(m.Key, original[m.Key]);
        }

        Thread.Sleep(2000);
        Emit("自检结束");

        try
        {
            File.WriteAllLines(reportPath, lines, Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>把异常写入 %APPDATA%\BrightnessTray\error.log，便于排查。</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrightnessTray");
            Directory.CreateDirectory(dir);
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "error.log"), line, Encoding.UTF8);
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
