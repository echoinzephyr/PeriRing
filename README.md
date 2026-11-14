# 环电 · PeriRing

用一圈，掌握外设电量。应用图标源文件为 `Assets/periring.png`，Windows 多尺寸图标为 `Assets/periring.ico`。

<img src="Assets/periring.png" width="112" alt="环电 PeriRing 图标">

Windows 托盘外设电量工具，使用 WinUI 3 与 Fluent 界面。所有已选在线设备共用一个分段环形图标，悬停显示紧凑的“设备名：电量”提示。

## 使用

从 [Releases](https://github.com/echoinzephyr/PeriRing/releases/latest) 下载 Windows x64 压缩包，完整解压后双击 `BatteryDisplay.exe`。自行构建时程序位于 `dist-winui`。默认只驻留托盘，点击图标查看紧凑浮层，右键可以刷新、管理设备、调整外观、检查更新或退出。图标可能收在任务栏右侧的 **^** 中，可以拖到任务栏常驻区域。

- 每 60 秒刷新，睡眠恢复后会重新读取。
- 右键勾选需要显示的设备；在“设备开关与排序”中拖动或上下移动设置优先级，设置自动保存。
- 1–5 个设备时均分圆环，每段长度表示对应电量；超过 5 个时，前三个设备显示曲线，其余每个设备各显示一个点，低电量点变红。
- 浮层可以拖动边缘调整大小，尺寸自动保存；失去焦点时自动收起，图钉可保持展开。
- 20% 及以下提醒一次，恢复到 25% 以上后允许再次提醒。
- 开机启动默认关闭，可以在右键菜单或设置中开启。
- 关闭设备窗口后仍在托盘运行，右键 **退出** 才会结束。
- 设备断开后隐藏图标，蓝牙列表中的旧读数标为“上次上报”。
- 点击“支持设备”查看品牌覆盖范围和各读取渠道状态。
- 重复打开程序会唤出已运行的设备窗口。
- 右键“检查更新”查询本仓库最新正式版，有新版时提供下载入口；下载后退出程序并替换文件，保留 `data` 文件夹中的设置。

## 支持范围

读取 Windows 保存的蓝牙外设电量；已连接的 BLE 设备没有 Windows 读数时，尝试读取标准 GATT Battery Service。设备和驱动没有提供电量时显示“—”，不会把未知电量显示成 0%。

程序按读取渠道和品牌协议分别适配。目前包含标准蓝牙、Windows 手柄、飞智官方后台服务，以及罗技、雷蛇、迈从、ATK/VGN/VXE/Pulsar、WLmouse、LAMZU、Glorious、华硕、赛睿、HyperX、Keychron、部分海盗船和 PlayStation 设备。覆盖范围以 [支持列表](SUPPORTED-DEVICES.md) 为准，同一品牌不代表全部型号都已支持。

**MCHOSE AX5 V2** 已在本机实测原始读数 **75%**，默认按官方驱动的分档规则显示 **80%**，可在设置中切换为原始读数。其他适配多数缺少对应硬件实测，检查通过不等于已经验证所有型号。飞智适配需要已安装官方驱动且 `SpaceStationService` 服务正在运行；不需要打开官方界面。

精确百分比、设备档位和电压估算分开标注。只有电压的罗技设备显示“约 XX%”和“按电压估算”；只有档位的 XInput、飞智或索尼设备不会显示为精确百分比。数字图标模式对没有精确百分比的设备显示问号，悬停查看档位；环形图标可表达其档位。

仅发送状态读取请求，不更改 DPI、灯效、按键或手柄工作模式。索尼蓝牙手柄采用监听方式：如果它尚未提供完整报告，显示未取得电量。某个读取渠道发生错误时，其余渠道仍继续工作。

本版本面向 Windows 10 2004 及以上，使用 x64 运行环境。压缩包需要 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) 和 [Windows App Runtime 2.6](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)（均选择 x64）。移动程序时请携带整个解压目录，程序需要同目录的支持文件，包括 `resources.pri` 和 `App.xbf`。

## 开发与验证

安装 .NET 10 SDK、Visual Studio Windows 应用开发组件，在 PowerShell 中运行 `./build.ps1`。

`BatteryDisplay.exe --show` 启动时打开设备窗口。

`BatteryDisplay.exe --diagnose` 读取真实设备，将结果及 HID 接口信息写入程序旁的 `diagnostics.json` 后退出。文件包含设备名称和标识，只保存在本机。

`BatteryDisplay.exe --self-test` 执行协议与图标检查，在程序旁生成 `self-test.json` 和图标预览。`--capture-ui` 读取真实设备并生成 WinUI 各页面截图后退出。

`BatteryDisplay.exe --check-update` 只检查最新版本并写入 `update-check.json`。更新请求只访问 GitHub 公开发布接口，不携带设备信息。

`./tools/Package-Release.ps1` 从新的发布目录生成 Windows x64 ZIP 和 SHA256 校验文件；包内包含运行文件、使用说明和许可证，不包含个人设置、设备诊断和构建缓存。

`IBatteryProvider` 隔离不同读取渠道；`IHidBatteryAdapter` 隔离各品牌的协议；`DeviceCatalog.json` 维护型号和设备标识。新增设备不需要改托盘界面。驱动的接口权限、固件版本及其他软件的占用情况可能影响实际读取。

设置保存在程序旁的 `data/settings.json`，应将程序放在可写目录。开启开机启动会登记当前程序路径，移动程序后需关闭再开启该选项。

## API 参考

- [Windows 设备信息属性](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/device-information-properties)
- [微软 Bluetooth GATT 客户端文档](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-client)
- [SetupDiGetDevicePropertyW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw)
- [迈从状态查询协议记录](https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md)
- [Windows 手柄电池报告](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.igamecontrollerbatteryinfo.trygetbatteryreport)
- [XInput 电池信息](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetbatteryinformation)
- [多品牌协议记录](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md)
- [飞智本地后台服务读取参考](https://github.com/JKWTCN/BatteryMonitor/blob/master/src/providers/xbox/FlydigiControllerReader.cpp)

协议与型号表参考来源及许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。本程序不会上传诊断结果或设备信息。

项目遵循仓库中的 [GPL-3.0 许可证](LICENSE)。版本变更见 [CHANGELOG.md](CHANGELOG.md)。
