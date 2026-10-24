# 可行性调查（2026-10-24，实施前）

## 阶段一结论

| 设备 | 证据与结论 | 下一步 |
|---|---|---|
| 达尔优 A950，2.4GHz | 本机 PnP 中有 260D:1074 和 260D:1084。公开源代码记录了 1074 的真实测试和私有 HID 查询协议；尚未在本机验证，不因设备标识相同就宣称支持。1084 的具体型号不能从共享 VID 推断。 | 最小诊断程序枚举产品字符串、集合和报告长度，独立查询每个接收器，验证响应头、长度、状态、校验和及百分比范围。 |
| 新盟 S75，2.4GHz | 官网分别列出 S75/S75 Pro，并指向 xinmeng.driveall.cn。尚无确认的本机 VID/PID、硬件版本、公开 SDK 或电量协议。 | 枚举本机 HID 描述信息，检查标准电池 Usage；分析官网公开网页驱动中的设备匹配和查询实现。不能确认时明确显示暂不支持。 |

## 已确认事实与限制

- A950 参考协议：17 字节，命令集合 FF02:0002，Feature Report 08，命令 04；回复来自 FF01:0000 Input Report，头 09，状态 byte2=00，数据长度 byte5，百分比 byte6，校验 byte16=(55-sum(byte0..15)) & FF。只发送这个有依据的查询命令。
- 原项目特别指出 FF04 / Report 06 固定出现 100，不能把它当真实电量。本项目禁止使用该路径。
- 充电标志 byte7 的硬件可靠性在参考 PR 中未确认，不能把它作为已验证的充电指示。
- 官方旧 A950 链接当前返回官网首页；新驱动页有多个 A950 变体。尚未确认原版 A950 官方驱动版本、UI 电量或公开集成 API；不运行不明版本安装包，不依赖厂商进程。
- USB 接收器存在只代表接收器在线，不代表鼠标/键盘已唤醒。无回复不能准确区分休眠、关机和无线断连。
- Windows HidP_GetCaps / HidP_GetValueCaps 能描述集合，HidD_GetFeature 能读取声明的 Feature Report，但 HID API 不会自动解码厂商私有百分比。
- 标准电池读数仅接受语义明确的 Battery Strength / Remaining Capacity，检查范围和报告解析结果；不能把无线信号、在线状态或普通整数当电量。

## 实施路线

先交付可导出 JSON 的只查询诊断工具，再根据真实实验结果实现 WinForms NotifyIcon 与深色两行浮层。公共模型分别表示有效、未知、接收器断连、失败、不支持、低电量警告；失败立即撤销当前百分比，历史有效值仅显示在诊断中。默认 60 秒更新，热插拔和系统唤醒触发去抖刷新，后台查询与 UI 分离；不开启自启动，提供用户级自启动设置。正式运行无需网络、管理员或厂商 UI。

## 第一手来源

1. [新盟 S75 官网](https://www.szxinmeng.com.cn/list_20/40.html)
2. [新盟驱动页：S75 与网页驱动对应关系](https://www.szxinmeng.com.cn/driver/)
3. [官方公开网页驱动](https://xinmeng.driveall.cn/)
4. [达尔优驱动页](https://cn.dareu.com/drivers/)
5. [A950 适配 PR #212，含实测与充电标志限制](https://github.com/HeyOkay/HaloBattery/pull/212)
6. [MousePower 协议来源，固定提交 684b5aa](https://github.com/vamyane/MousePower/blob/684b5aa70e36952bac1cda094291c2ccd39c1e34/src/mousepower/core/providers/dareu_compx.py)
7. [Microsoft：HID 集合能力](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/collection-capability)
8. [Microsoft：HidD_GetFeature](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getfeature)

## 仍需验证

本机两种接收器的型号对应、S75 版本、读数与厂商显示对照、设备物理断连和睡眠唤醒、DPI 多屏以及长期稳定性均需要单独记录，不用其他设备的成功代替。
