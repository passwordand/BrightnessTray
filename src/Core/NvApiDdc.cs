using System.Runtime.InteropServices;

namespace BrightnessTray.Core;

/// <summary>
/// NVIDIA 原生 DDC/CI 通道。
///
/// 本机 DXVA2 会把两个物理显示器映射到同一条通道（两个 hPhysicalMonitor 都为 0），
/// 导致副屏可调、主屏写入却落到副屏。NvAPI 用 GPU + displayMask 精确寻址，
/// 实测可分别读写主屏 0x100 与副屏 0x200。
///
/// 数据包格式与 NVIDIA 官方 i2c 示例一致：先写查询包，再单独读响应。
/// </summary>
internal static class NvApiDdc
{
    /// <summary>一条具体的 NVIDIA 显示器通道。</summary>
    internal readonly record struct Channel(IntPtr Gpu, uint DisplayMask);

    private static readonly object Gate = new();
    private static bool _initialized;
    private static bool _unavailable;

    private const uint IdInitialize = 0x0150E828;
    private const uint IdUnload = 0xD22BDD7E;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdEnumNvidiaDisplayHandle = 0x9ABDD40D;
    private const uint IdGetAssociatedDisplayName = 0x22A78B05;
    private const uint IdGetAssociatedDisplayOutputId = 0xD995937E;
    private const uint IdGetPhysicalGpusFromDisplay = 0x34EF9506;
    private const uint IdI2CRead = 0x2FDE12C5;
    private const uint IdI2CWrite = 0xE812EB07;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate(IntPtr[] handles, ref uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumNvidiaDisplayHandleDelegate(int index, ref IntPtr display);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetAssociatedDisplayNameDelegate(IntPtr display, IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetAssociatedDisplayOutputIdDelegate(IntPtr display, out uint outputId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPhysicalGpusFromDisplayDelegate(IntPtr display, IntPtr[] gpus, ref uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int I2CDelegate(IntPtr gpu, IntPtr info);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct I2CInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DeviceAddress;
        public IntPtr RegisterAddress;
        public uint RegisterSize;
        public IntPtr Data;
        public uint DataSize;
        public uint SpeedDeprecated;
        public uint SpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    [DllImport("nvapi64.dll")]
    private static extern IntPtr nvapi_QueryInterface(uint id);

    /// <summary>
    /// 建立 GDI 设备名（如 \\.\DISPLAY2）到 NVIDIA 通道的映射。
    /// 没有 NVIDIA 驱动或没有 NVIDIA 显示器时返回空表，调用方回退到 DXVA2。
    /// </summary>
    public static Dictionary<string, Channel> CreateChannelMap()
    {
        var result = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);

        lock (Gate)
        {
            if (!EnsureInitialized())
            {
                return result;
            }

            try
            {
                var gpus = new IntPtr[64];
                uint gpuCount = 0;
                int status = Call<EnumPhysicalGpusDelegate>(IdEnumPhysicalGpus)(gpus, ref gpuCount);
                if (status != 0 || gpuCount == 0)
                {
                    return result;
                }

                var enumDisplays = Call<EnumNvidiaDisplayHandleDelegate>(IdEnumNvidiaDisplayHandle);
                var getName = Call<GetAssociatedDisplayNameDelegate>(IdGetAssociatedDisplayName);
                var getOutputId = Call<GetAssociatedDisplayOutputIdDelegate>(IdGetAssociatedDisplayOutputId);
                var getGpus = Call<GetPhysicalGpusFromDisplayDelegate>(IdGetPhysicalGpusFromDisplay);

                for (int index = 0; index < 64; index++)
                {
                    IntPtr display = IntPtr.Zero;
                    status = enumDisplays(index, ref display);
                    if (status == -7) // NVAPI_END_ENUMERATION
                    {
                        break;
                    }

                    if (status != 0 || display == IntPtr.Zero)
                    {
                        continue;
                    }

                    IntPtr nameBuffer = Marshal.AllocHGlobal(64);
                    string deviceName;
                    try
                    {
                        status = getName(display, nameBuffer);
                        if (status != 0)
                        {
                            continue;
                        }

                        deviceName = Marshal.PtrToStringAnsi(nameBuffer) ?? string.Empty;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(nameBuffer);
                    }

                    if (deviceName.Length == 0)
                    {
                        continue;
                    }

                    status = getOutputId(display, out uint outputId);
                    if (status != 0 || outputId == 0)
                    {
                        continue;
                    }

                    IntPtr gpu = IntPtr.Zero;
                    var displayGpus = new IntPtr[64];
                    uint displayGpuCount = 0;
                    status = getGpus(display, displayGpus, ref displayGpuCount);
                    if (status == 0 && displayGpuCount > 0)
                    {
                        gpu = displayGpus[0];
                    }
                    else if (gpuCount == 1)
                    {
                        // 单显卡机器上可安全回退到唯一 GPU。
                        gpu = gpus[0];
                    }

                    if (gpu != IntPtr.Zero)
                    {
                        result[deviceName] = new Channel(gpu, outputId);
                    }
                }
            }
            catch (Exception ex)
            {
                DdcEngine.Trace("NVAPI 枚举失败，将回退 DXVA2：" + ex.Message);
            }

            return result;
        }
    }

    /// <summary>读取指定显示器的亮度。</summary>
    public static bool TryGetBrightness(
        Channel channel,
        out int percent,
        out uint max,
        out string? error)
    {
        percent = 0;
        max = 100;
        error = null;

        lock (Gate)
        {
            if (!EnsureInitialized())
            {
                error = "NVAPI 不可用";
                return false;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    int status = WritePacket(channel, [0x82, 0x01, 0x10, 0xAC]);
                    if (status == 0)
                    {
                        Thread.Sleep(100);
                        status = ReadResponse(channel, out byte[] response);
                        if (status == 0 && TryParseBrightness(response, out percent, out max))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Thread.Sleep(60);
            }

            error ??= "NVIDIA DDC 读取失败";
            return false;
        }
    }

    /// <summary>把指定显示器的亮度写为原始 VCP 值。</summary>
    public static bool SetBrightness(Channel channel, uint value, out string? error)
    {
        error = null;

        lock (Gate)
        {
            if (!EnsureInitialized())
            {
                error = "NVAPI 不可用";
                return false;
            }

            try
            {
                byte[] packet =
                [
                    0x84,
                    0x03,
                    0x10,
                    (byte)(value >> 8),
                    (byte)(value & 0xFF),
                    0xBC,
                ];

                int status = WritePacket(channel, packet);
                if (status != 0)
                {
                    error = $"NVAPI 写入失败（{status}）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>进程退出时释放 NVAPI 引用。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                try
                {
                    Call<UnloadDelegate>(IdUnload)();
                }
                catch
                {
                    // 退出阶段忽略
                }

                _initialized = false;
            }
        }
    }

    private static bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        if (_unavailable)
        {
            return false;
        }

        try
        {
            IntPtr entry = nvapi_QueryInterface(IdInitialize);
            if (entry == IntPtr.Zero)
            {
                _unavailable = true;
                return false;
            }

            int status = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(entry)();
            if (status != 0)
            {
                _unavailable = true;
                return false;
            }

            _initialized = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            _unavailable = true;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _unavailable = true;
            return false;
        }
    }

    private static T Call<T>(uint id) where T : Delegate
    {
        IntPtr entry = nvapi_QueryInterface(id);
        if (entry == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException($"NVAPI 接口 0x{id:X} 不存在");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }

    /// <summary>写一笔 DDC 请求或设置包（设备地址 0x6E、寄存器 0x51）。</summary>
    private static int WritePacket(Channel channel, byte[] data)
    {
        return InvokeI2C(channel, write: true, deviceAddress: 0x6E, register: [0x51], data);
    }

    /// <summary>读取 11 字节亮度响应（设备地址 0x6F）。</summary>
    private static int ReadResponse(Channel channel, out byte[] response)
    {
        response = new byte[11];
        return InvokeI2C(channel, write: false, deviceAddress: 0x6F, register: [0], response);
    }

    private static int InvokeI2C(Channel channel, bool write, byte deviceAddress, byte[] register, byte[] data)
    {
        int size = Marshal.SizeOf<I2CInfoV3>();
        IntPtr registerPtr = Marshal.AllocHGlobal(register.Length);
        IntPtr dataPtr = Marshal.AllocHGlobal(Math.Max(16, data.Length));
        IntPtr infoPtr = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.Copy(register, 0, registerPtr, register.Length);
            Marshal.Copy(data, 0, dataPtr, data.Length);

            // 发送包的最后一个字节是校验和；读取响应时覆盖与否无关紧要。
            if (data.Length > 0)
            {
                byte checksum = deviceAddress;
                foreach (byte b in register)
                {
                    checksum ^= b;
                }

                for (int i = 0; i < data.Length - 1; i++)
                {
                    checksum ^= data[i];
                }

                Marshal.WriteByte(dataPtr, data.Length - 1, checksum);
            }

            var info = new I2CInfoV3
            {
                // MAKE_NVAPI_VERSION：低 16 位为结构大小，高 16 位为版本号。
                Version = (uint)size | (3u << 16),
                DisplayMask = channel.DisplayMask,
                IsDdcPort = 1,
                DeviceAddress = deviceAddress,
                RegisterAddress = registerPtr,
                RegisterSize = (uint)register.Length,
                Data = dataPtr,
                DataSize = (uint)data.Length,
                SpeedDeprecated = 0,
                SpeedKhz = 27,
                PortId = 0,
                IsPortIdSet = 0,
            };

            Marshal.StructureToPtr(info, infoPtr, false);

            var function = Call<I2CDelegate>(write ? IdI2CWrite : IdI2CRead);
            int status = function(channel.Gpu, infoPtr);
            Marshal.Copy(dataPtr, data, 0, data.Length);
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(registerPtr);
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    private static bool TryParseBrightness(byte[] response, out int percent, out uint max)
    {
        percent = 0;
        max = 100;

        // 响应布局：6E 88 02 00 10 00 maxHi maxLo curHi curLo checksum
        if (response.Length < 11 || response[0] != 0x6E || response[3] != 0 || response[4] != 0x10)
        {
            return false;
        }

        uint maximum = (uint)(response[6] << 8 | response[7]);
        uint current = (uint)(response[8] << 8 | response[9]);
        if (maximum == 0)
        {
            return false;
        }

        max = maximum;
        percent = Math.Clamp((int)Math.Round(current * 100.0 / maximum), 0, 100);
        return true;
    }
}
