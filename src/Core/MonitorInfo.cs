namespace BrightnessTray.Core;

/// <summary>
/// 一台可调亮度的显示器的只读快照，供界面线程安全使用（不持有原生句柄）。
/// </summary>
public sealed class MonitorInfo
{
    /// <summary>稳定标识（设备接口路径），用于记住每台屏幕的亮度。</summary>
    public required string Key { get; init; }

    /// <summary>显示名称，如 "U28G2G6B"。</summary>
    public required string Name { get; init; }

    /// <summary>显示设备名，如 "\\.\DISPLAY2"。</summary>
    public required string DeviceName { get; init; }

    /// <summary>是否为主显示器。</summary>
    public bool IsPrimary { get; init; }

    /// <summary>当前亮度百分比（0-100）。</summary>
    public int Percent { get; init; }

    /// <summary>DDC/CI 亮度通道是否可用。</summary>
    public bool DdcSupported { get; init; }

    /// <summary>不可用时的原因描述。</summary>
    public string? Error { get; init; }

    /// <summary>菜单里显示的标题，例如 "U28G2G6B（主屏）"。</summary>
    public string DisplayTitle => IsPrimary ? $"{Name}（主屏）" : Name;
}
