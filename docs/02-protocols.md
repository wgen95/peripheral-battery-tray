# 读取协议与验证依据

调查与测试日期：2026-10-24。公开驱动资产随时可能更新，下面标出测试时资产名。本项目运行时不下载网页驱动，不依赖网络。

## A950

来源为 [MousePower 固定提交](https://github.com/vamyane/MousePower/blob/684b5aa70e36952bac1cda094291c2ccd39c1e34/src/mousepower/core/providers/dareu_compx.py) 及 [HaloBattery PR #212](https://github.com/HeyOkay/HaloBattery/pull/212)。后者仍是公开 PR，不作为厂商保证。

- VID 260D、PID 1074，Feature 集合 FF02:0002、Input 集合 FF01:0000，报告均为 17 字节。
- 发出 `08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49`。
- 回复要求长度 17、头 09、命令 04、状态 00、数据长度 02；byte6 范围 0–100。
- 最后一字节必须等于 `(0x55 - sum(bytes[0..15])) & 0xff`。
- 本机有效采样：`09 04 00 00 00 02 32 01 00 00 00 00 00 00 00 00 13`，即设备报告 50%。
- 通过 Windows USB 父接口实例匹配查询与响应集合，防止跨接收器配对；不以 VID/PID 作为唯一物理设备身份。
- 不读取 FF04/Report06，它在来源中被指出会返回固定 100。byte7 充电语义的硬件可靠性仍待验证，因此 UI 不显示充电结论。
- 本机还枚举到 1084，单独查询同样通过。无法仅凭通用产品字符串判断它是否为同一鼠标的另一接口。默认只选择唯一的 1074；1084 需要用户选择。

## S75

第一手证据：[新盟官网驱动目录](https://www.szxinmeng.com.cn/driver/) → [官方网页驱动](https://xinmeng.driveall.cn/)。测试时公开资产 `/assets/layout-classic-C50ozYno.js` 中的 `EJ`（帧构造）、`TJ`（交换）、`LJ`（设备信息解析）和 S75 配置形成相互印证。

- 官网配置路径 `3141-65274-2.4G Dongle-2-15` 对应 S75 标准版。不能将所有 0C45:FEFA 都识别为 S75。
- 本机集合 FF60:0061，Input / Output report 都为 65 字节，Report ID 0，64 字节载荷。没有 Feature Report。
- Windows 输出为 `00 AA 10 30 00 00 00 01 00` 后补零到 65 字节。只有 GET_DEVICE_INFO 查询，不发送初始化、灯光、宏、升级或复位命令。
- Windows 输入必须为 65 字节，Report ID 0，载荷以 `55 10 30 00 00` 开头；偏移 8 后为设备信息载荷（不计 Report ID）。
- 检查载荷内 VID=0C45、PID=8006、manufacturer=2、product=15；仅在全部匹配时显示载荷 byte17 的 0–100 电量。
- 首次载荷采样前 20 字节：`40 00 00 0C 45 0C 06 80 31 01 00 00 02 00 0F 00 00 39 00 00`，其中 `39` 即 57%。后续真实读到 56%。
- 标准描述符只声明厂商自定义数据块，无法用标准 HID Battery Strength 解码。
- 官方 `HJ` 将 `55 FC 04` 识别为无线断连，`WJ` 将 `55 FC 06` 识别为休眠。本机已捕获 04，UI 显示设备断连；06 仅有公开协议依据，尚未真实捕获。既不是电量，也不能取后续内存残留中的整数作为电量。
- 这套设备信息协议没有在已查阅实现中发现电池字段的独立校验和，因此使用严格长度、命令、地址、内部身份和范围校验。仍需厂商 UI 对照验证电量量化精度。

## Windows 接口与隔离

使用 SetupDi 枚举当前 HID 接口，CM_Get_Parent 匹配 USB 父接口，HidD_GetAttributes / HidP_GetCaps / HidP_GetValueCaps 检查报告能力。主程序以异步子进程查询，8 秒无结果则终止子进程。每个设备查询独立捕获错误；单通道响应窗口约 1.2 秒。无有效读数就清除当前百分比，历史值只在诊断中标明时间和接口。

设备热插拔和恢复广播触发 1.8 秒去抖刷新；每次查询打开并关闭句柄，不保留失效的 USB 句柄。常驻程序的主线程不直接调用可能阻塞的厂商 HID 函数。未采用内核驱动、进程注入、内存读取或固件修改。

Microsoft 参考：[集合能力](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/collection-capability)、[HidD_GetFeature](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getfeature)、[解析报告](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/interpreting-hid-reports)。
