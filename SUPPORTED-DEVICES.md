# 首批设备支持范围

当前版本按协议适配，不绑定某一台电脑。表中“待实测”表示代码与协议解析已接入、已检查，但本项目尚未用对应硬件验证。不能理解为该品牌全系列兼容。

| 品牌 / 接口 | 当前覆盖范围 | 验证情况 |
|---|---|---|
| 标准蓝牙 | Windows 已上报电量的鼠标、键盘、耳机等；标准 BLE Battery Service | 本机系统读数已验证；BLE 直读待实测 |
| 迈从 | Realtek 协议的 AX5 V2、部分 M7 / L7、A7 V2 系列 | AX5 V2 接收器原始 75%，按官方分档显示 80%；其余待实测 |
| 瓦尔基里 / SONiX 键盘 | VK75、VK99 / VK99 Pro 已登记接口；接收器必须同时匹配名称、接口和报告格式 | VK75 有线识别已接入，有线没有读数时显示原因；2.4G 电量查询待对应硬件实测 |
| 罗技 | 提供 HID++ 电池功能的 Unifying / Bolt / Lightspeed / Nano 外设；部分 G 系列耳机 | 待实测；旧式电池寄存器、新式百分比、档位和电压均有对应处理 |
| 雷蛇 | 已登记的 Viper、DeathAdder、Basilisk、Naga、Cobra、Pro Click 等无线鼠标；BlackWidow、DeathStalker、Pro Type Ultra 等无线键盘 | 待实测；具体 USB 标识以设备目录为准，不含 Mouse Dock Pro 的转发协议 |
| ATK / VGN / VXE | 已登记的 A9、X1、R1、Dragonfly 4K 等 CompX 设备 | 待实测；并非所有新款均使用此协议 |
| Pulsar / Hitscan / Scyrox / G-Wolves | 设备目录中登记的 CompX 接收器 / 有线标识 | 待实测 |
| WLmouse | Beast X、Beast X Max、Beast X Mini Pro 的已登记接收器 | 待实测 |
| LAMZU | Maya X 的已登记 USB 标识 | 待实测 |
| Glorious | Model O Wireless 接收器 | 待实测 |
| 迈从 A5 | A5 Pro Max 的 1K / 4K 接收器和有线标识 | 待实测，使用独立协议 |
| 华硕 ROG / TUF | 设备目录登记的 Harpe、Keris、Gladius、Chakram、Spatha、Pugio、TUF M4 等 | 待实测；旧型号只提供档位 |
| 赛睿 | 已登记的 Nova 7、Nova 5、Nova 3 Wireless、Arctis 7+、GameBuds | 待实测，区分接收器在线状态与缓存值 |
| HyperX | Cloud III Wireless、Cloud II Core Wireless | 待实测 |
| Keychron | M5、Ultra-Link 8K、Nape Pro 的已登记 USB 标识 | 待实测，不代表所有 Keychron 键盘 |
| 海盗船 | Dark Core RGB Pro SE 接收器、K65 PLUS Wireless 接收器；Sabre v2 Pro Ultralight 由 CompX 接入 | 待实测 |
| Xbox / XInput | 向系统上报有效电池类型和档位的控制器；提供有效 Windows 电池报告的控制器 | 待实测；无线手柄被驱动标为有线时不虚构电量 |
| 飞智 | 官方 SpaceStationService 能识别并上报的 Apex / Vader / Direwolf 等 | 服务协议已接入，待硬件实测；需已有官方驱动和后台服务 |
| PlayStation | DualShock 4、DualSense、DualSense Edge | 待实测；蓝牙仅监听已有报告，不主动切换增强模式 |

`DeviceCatalog.json` 保存精确的 VID/PID 型号目录；动态协议适配器还有自己的能力检查和匹配条件。设备“被识别”不代表“本次已经取得有效电量”。未知回应、损坏校验、离线接收器缓存等会被拒绝。

每个物理接收器的设备实例都保留在标识里，同款设备不会仅因名字相同就被合并。所有已选在线设备共用一个分段环形托盘图标。跨蓝牙、2.4G 与有线连接的同一外设，目前不保证都能自动合并为同一条设备记录。

后续适配可以增加型号目录条目或新的 `IHidBatteryAdapter`；现有协议不会向表外设备试发未知命令。
