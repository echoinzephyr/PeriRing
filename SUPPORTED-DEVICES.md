# 首批设备支持范围

当前版本按协议适配，不绑定某一台电脑。表中“待实测”表示代码与协议解析已接入、已检查，但本项目尚未用对应硬件验证。不能理解为该品牌全系列兼容。

| 品牌 / 接口 | 当前覆盖范围 | 验证情况 |
|---|---|---|
| 标准蓝牙 | Windows 已上报电量的鼠标、键盘、耳机等；标准 BLE Battery Service | 本机系统读数已验证；BLE 直读待实测 |
| 迈从 | AX5 V2、M7 / M7 Pro / M7 Ultra、L7 / L7 Pro / L7 Pro+ / L7 Ultra / L7 Ultra+、A7 / A7 Pro / A7 Ultra / A7 Ultra(RE)、A7 V2 Pro / Pro+ / Ultra / Ultra+ 的已登记有线接口及部分 Realtek 接收器 | AX5 V2 和 A7 Ultra(RE) 已在环电实测 2.4G，AX5 V2 有线已在环电实测。其余有线条目按公开协议接入，待环电实测 |
| 瓦尔基里 / SONiX 键盘 | VK75、VK99 / VK99 Pro 已登记接口；接收器必须同时匹配名称、接口和报告格式 | VK75 通过 2.4G 接收器正常显示电量，已获用户实测确认；USB 有线目前仅验证型号识别，VK99 / VK99 Pro 电量仍待实测 |
| 罗技 | GPW（G PRO Wireless）、GPX（G PRO X SUPERLIGHT）、GPX 2 / 2 DEX、G502 LIGHTSPEED / X LIGHTSPEED / X PLUS 的已登记 USB 和 Lightspeed 接收器。也支持提供 HID++ 电池功能的 Unifying、Bolt、Nano 外设及部分 G 系列耳机 | GPW 一代的电压会标为估算，GPX 系列可读取设备上报的百分比。已有公开 HID++ 读数，待环电实机验证 |
| 雷蛇 | 已登记的 Viper、DeathAdder、Basilisk、Naga、Cobra、Pro Click 鼠标及部分无线键盘；已核对的鼠标接口区分 USB 与 2.4G | Viper V4 Pro 有线与接收器有公开抓包，待环电实测。可用接口以设备目录为准 |
| ATK / VGN / VXE | 已登记的 A9、X1、R1、Dragonfly 4K 等 CompX 设备；已登记的有线与接收器接口会分别显示 USB 或 2.4G | VXE R1 Pro Max 有第三方有线和 2.4G 实测，待环电实测 |
| ATK 键盘 | V75X USB 有线，校验产品名、接口及请求编号 | 有公开有线充电回包，待环电实测 |
| NZXT | Lift Elite Wireless USB 有线和 2.4G 接收器 | 两种连接均有公开电量与充电记录，待环电实测 |
| Pulsar | X2 V2 Mini、X2 CrazyLight Mini 的已登记 USB 及 2.4G 接口 | X2 CrazyLight Mini 有第三方双模式实测，待环电实测 |
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
| 飞智 | 新版 SpaceStationService 的电量档位 | 服务协议待硬件实测；旧版空间站 3 暂不支持，无法可靠读取的估算值不显示 |
| PlayStation | DualShock 4、DualSense、DualSense Edge | 待实测；蓝牙仅监听已有报告，不主动切换增强模式 |

`DeviceCatalog.json` 保存精确的 VID/PID 型号目录；动态协议适配器还有自己的能力检查和匹配条件。设备“被识别”不代表“本次已经取得有效电量”。未知回应、损坏校验、离线接收器缓存等会被拒绝。

有线适配参考 [MCHOSE 协议记录](https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md)、[Halo Battery 的协议与实测记录](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md) 和 [OpenRazer 的连接标识](https://github.com/openrazer/openrazer#mouse-devices)。

罗技读数参考 Solaar 的 [GPW 电压](https://github.com/pwr-Solaar/Solaar/issues/2667)、[GPX 百分比](https://github.com/pwr-Solaar/Solaar/issues/3005)、[GPX 2 回包](https://github.com/pwr-Solaar/Solaar/issues/2207) 和 [GPX 2 DEX 有线充电记录](https://github.com/pwr-Solaar/Solaar/issues/2836)。

每个物理接收器的设备实例都保留在标识里，同款设备不会仅因名字相同就被合并。所有已选在线设备共用一个分段环形托盘图标。跨蓝牙、2.4G 与有线连接的同一外设，目前不保证都能自动合并为同一条设备记录。

后续适配可以增加型号目录条目或新的 `IHidBatteryAdapter`；现有协议不会向表外设备试发未知命令。
