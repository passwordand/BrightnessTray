using System.Text.Json;

namespace BrightnessTray.Core;

/// <summary>
/// 用户设置的持久化（保存在 %APPDATA%\BrightnessTray\settings.json）。
/// 目前只记住「开机自动恢复上次亮度」开关和每台显示器的最后亮度。
/// </summary>
public sealed class AppSettings
{
    /// <summary>启动时是否把每台显示器恢复为上次使用的亮度。</summary>
    public bool RestoreOnStartup { get; set; } = true;

    /// <summary>显示器 key → 上次亮度百分比。</summary>
    public Dictionary<string, int> LastBrightness { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string SettingsPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrightnessTray");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
            if (loaded is null)
            {
                return new AppSettings();
            }

            // 反序列化后的字典丢失比较器，这里重新包一层
            loaded.LastBrightness = new Dictionary<string, int>(loaded.LastBrightness, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        catch
        {
            // 配置损坏时用默认值，不影响启动
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 保存失败不弹窗，静默忽略
        }
    }
}
