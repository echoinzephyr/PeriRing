# MCHOSE V9 Turbo+ 实机验证

2026-11-22，维护者收到 Test 1 的实机反馈。V9 Turbo+ 通过 2.4G 接收器显示 50%，测试者确认电量正确。截图同时显示蓝牙连接的 Q5，读数为 94%。这次反馈覆盖正常无线连接、百分比显示和多设备同时显示。充电切换、休眠唤醒及拔出接收器的实机结果待补录。[Issue #1](https://github.com/echoinzephyr/PeriRing/issues/1) · [PR #2](https://github.com/echoinzephyr/PeriRing/pull/2)

Test 1 基于 v0.1.4 和 `e9f498d`，并增加了同一接收器的 HID 集合选择与回复等待逻辑。适配匹配 `3837:600A` 的 `MI_00` 厂商 HID 集合，通过输入和输出报告长度筛选接口。每次发送电量查询后，最多等待 1 秒并跳过无关回复，同一轮扫描最多查询 3 次。[OpenMHub 协议实现](https://github.com/brayanspagnol/OpenMHub/blob/f77c000334ad9e34499d7acab315a0d9ee8789b7/app/renderer/drivers/headset.js)

回归检查覆盖同一接收器的多集合选择、600 毫秒的延迟回包、无关报告、读取取消，以及放电、充电、充满和休眠状态解析。
