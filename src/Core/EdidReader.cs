using System.Text;
using Microsoft.Win32;

namespace BrightnessTray.Core;

/// <summary>
/// 从注册表中的 EDID 数据里解析显示器友好名称（EDID 描述符 tag 0xFC）。
/// 这样菜单里能显示 "U28G2G6B" 这类真实型号，而不是 "显示器 1"。
/// </summary>
internal static class EdidReader
{
    /// <summary>
    /// 由设备接口路径（\\?\DISPLAY#AOC2802#5&amp;85717ca&amp;0&amp;UID4352#{guid}）反查 EDID 名称。
    /// </summary>
    public static string? GetMonitorName(string? deviceInterfacePath)
    {
        if (string.IsNullOrWhiteSpace(deviceInterfacePath))
        {
            return null;
        }

        try
        {
            const string prefix = @"\\?\";
            if (!deviceInterfacePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            // 设备接口路径形如：
            //   \\?\DISPLAY#AOC2802#5&85717ca&0&UID4352#{e6f07b5f-...}
            // 去掉前缀后分段为：设备类 / 硬件 ID / 实例 ID / 接口 GUID
            // 对应的注册表枚举键是 DISPLAY\{硬件ID}\{实例ID}
            string[] parts = deviceInterfacePath[prefix.Length..].Split('#');
            if (parts.Length < 3)
            {
                return null;
            }

            string subKey = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters";
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey);
            if (key?.GetValue("EDID") is not byte[] edid)
            {
                return null;
            }

            return ParseFriendlyName(edid);
        }
        catch
        {
            // 读不到就用调用方的兜底名称，不影响功能
            return null;
        }
    }

    /// <summary>解析 EDID 中 4 个 18 字节描述符里的显示器名称。</summary>
    private static string? ParseFriendlyName(byte[] edid)
    {
        if (edid.Length < 128)
        {
            return null;
        }

        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (offset + 17 >= edid.Length)
            {
                break;
            }

            // 描述符前 3 字节为 0 且第 4 字节为 0xFC 时，表示显示器名称
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != 0xFC)
            {
                continue;
            }

            var sb = new StringBuilder(13);
            for (int i = offset + 5; i < offset + 18; i++)
            {
                byte b = edid[i];
                if (b == 0 || b == 0x0A)
                {
                    break;
                }

                if (b >= 0x20)
                {
                    sb.Append((char)b);
                }
            }

            string name = sb.ToString().Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return null;
    }
}
