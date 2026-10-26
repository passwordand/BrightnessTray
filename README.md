# BrightnessTray · 屏幕亮度托盘

一个常驻 Windows 通知区域的小工具，用来**单独或同时调节多台显示器的硬件亮度**。

通过 DDC/CI（MCCS VCP `0x10`）直接控制显示器背光，效果等同于按显示器上的实体按键，
不是软件滤镜，也不会影响截图和录屏的画面亮度。

- 每块屏幕一条独立滑块，互不干扰
- 「全部屏幕」总控，一次调完所有屏幕
- 托盘太阳图标随当前亮度变化
- 记住每屏亮度，可开机自动恢复
- 支持热插拔、分辨率变化、锁屏解锁与睡眠唤醒

## 界面

面板（全部屏幕 / 副屏 / 主屏）：

<img src="docs/panel.png" width="430" alt="亮度调节面板">

托盘图标：

<img src="docs/tray.png" width="240" alt="托盘太阳图标">

## 快速开始

### 环境要求

- Windows 10 / 11，x64
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)

### 运行

```powershell
# 克隆
git clone https://github.com/passwordand/BrightnessTray.git
cd BrightnessTray

# 编译
dotnet build src/BrightnessTray.csproj -c Release

# 启动
.\Start BrightnessTray.cmd
```

也可以直接运行：

```powershell
.\src\bin\Release\net10.0-windows\BrightnessTray.exe
```

启动后托盘会出现太阳图标：

- **左键**：打开亮度面板
- **右键**：预设档位、全部调亮/调暗、开机启动、退出
- 点击面板外部或按 `Esc` 关闭

## 单屏自检

不打开界面也能验证硬件写入是否生效。自检会读取原值 → 写入目标值 → 回读确认 → 恢复原值，
只会修改匹配到的屏幕：

```powershell
$exe = '.\src\bin\Release\net10.0-windows\BrightnessTray.exe'

# 只测主屏（按型号名匹配）
& $exe --selftest 55 U28G2G6B

# 只测副屏
& $exe --selftest 55 H24T7

# 测所有屏幕
& $exe --selftest 55
```

报告写入 `%APPDATA%\BrightnessTray\selftest.txt`。

## 实现说明

### 为什么优先用 NVIDIA 原生 I2C

在部分 NVIDIA + Intel 混合输出环境中，DXVA2 的
`GetPhysicalMonitorsFromHMONITOR` 会让两块显示器共享同一条 DDC 通道：

- 两个 `hPhysicalMonitor` 都返回 `0x0`
- 给 A 屏写入，B 屏读数也变
- 两台显示器返回完全相同的 MCCS capabilities 字符串

结果就是「副屏能调、主屏没反应」。

因此本工具优先使用 `nvapi64.dll` 的原生 I2C 接口：

1. `NvAPI_EnumNvidiaDisplayHandle` 枚举 NVIDIA 显示器
2. `NvAPI_GetAssociatedDisplayOutputId` 取得该显示器独立的 `displayMask`
3. `NvAPI_I2CWrite` / `NvAPI_I2CRead` 按 GPU + `displayMask` 精确读写

没有 NVAPI 的环境自动回退到 DXVA2。

### 其它设计要点

- **独占后台线程**：DDC 走 I2C，单次调用几十毫秒，放 UI 线程会让滑块卡顿。
  连续拖动只保留最新目标值，不堆积请求。
- **显示器识别**：用设备接口路径作为稳定 key，并从 EDID 的 `0xFC` 描述符解析真实型号名，
  所以菜单里显示 `U28G2G6B` 而不是「显示器 1」。
- **点击外部关闭**：使用 `WH_MOUSE_LL` 低层鼠标钩子，比依赖窗口 `Deactivate` 稳定，
  拖动滑块时不会被误关。

## 已在以下环境实测

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 11 专业版 26200 |
| 主屏 | AOC U28G2G6B，3840×2160，`displayMask=0x100` |
| 副屏 | SKG H24T7，`displayMask=0x200` |
| 显卡 | NVIDIA RTX 4070 Ti + Intel UHD 770 |

验证结果：

- 主屏写入 60 → 回读 60；写入 85 → 回读 85
- 调主屏期间副屏保持 75，未被误改
- 应用自检：主屏单独设为 55 / 70 / 100 均成功

## 已知限制

- 显示器 OSD 菜单中必须开启 **DDC/CI**。
- **锁屏或休眠期间** DDC 通道可能被系统占用，读写会失败；解锁或唤醒后程序会自动重新检测。
- 个别显示器对写入频率敏感，拖动过快时可能丢一两次写入，松手后以最后一次值为准。
- 输出会按显示器各自的 `max` 值换算，不同型号的同一百分比观感可能不同。
- 目前只实现亮度通道，对比度等其它 VCP 可按同样方式扩展。

## 项目结构

```
BrightnessTray/
├─ src/
│  ├─ BrightnessTray.csproj        项目文件（.NET 10 WinForms，无第三方依赖）
│  ├─ app.manifest                 清单（asInvoker，不需要管理员）
│  ├─ Program.cs                   入口、单实例、全局异常日志、自检
│  ├─ TrayApplicationContext.cs    托盘图标、菜单、热插拔与锁屏/唤醒监听
│  ├─ Core/
│  │  ├─ DdcEngine.cs              DDC/CI 引擎（独立后台线程，写入合并）
│  │  ├─ NvApiDdc.cs               NVIDIA 原生 I2C 通道
│  │  ├─ MonitorInfo.cs            显示器快照模型
│  │  ├─ EdidReader.cs             从注册表 EDID 解析型号名
│  │  └─ AppSettings.cs            配置持久化
│  ├─ Interop/NativeMethods.cs     Win32 / DXVA2 声明
│  └─ UI/
│     ├─ BrightnessPopup.cs        亮度面板
│     └─ TrayIconFactory.cs        动态绘制太阳图标
├─ docs/                           文档配图
├─ Start BrightnessTray.cmd        双击启动
├─ LICENSE                         MIT
└─ README.md
```

## 诊断

- 自检报告：`%APPDATA%\BrightnessTray\selftest.txt`
- 异常日志：`%APPDATA%\BrightnessTray\error.log`
- 诊断追踪：`%APPDATA%\BrightnessTray\trace.log`

遇到某块屏不生效时，建议先跑只针对该屏的 `--selftest`，确认是 DDC 通道问题还是界面问题。

## 许可证

[MIT](LICENSE)
