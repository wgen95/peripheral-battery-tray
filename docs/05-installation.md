# 安装与卸载

从 [v2.1.0 Release](https://github.com/wgen95/peripheral-battery-tray/releases/tag/v2.1.0) 下载 `PeripheralBattery-2.1.0-Setup-x64.exe`，双击按中文向导安装。无需单独安装 .NET，也不要求管理员权限。

- 默认安装到当前用户的 `%LOCALAPPDATA%/Programs/PeripheralBattery`。
- 自动添加“无线外设电量”开始菜单入口，可以自行勾选桌面快捷方式。
- 安装完成后可直接启动；日常只在系统托盘运行。v2 的常驻组件为 PeripheralBattery.exe，查询 / 设置组件为 PeripheralBattery.Backend.exe；后者按需启动，不应单独常驻。
- 浮层名称固定显示 `A950`、`S75`，不显示“鼠标”“键盘”等前缀。
- 关闭电量面板不会退出应用。退出请使用托盘右键菜单。
- 更新时运行新版安装包，沿用安装目录；安装包会尝试让旧实例正常退出，不强制结束进程。

删除时打开 **Windows 设置 → 应用 → 已安装的应用 → 无线外设电量 → 卸载**。卸载会正常退出应用，移除安装文件、开始菜单 / 桌面快捷方式、自启动项和应用列表登记。

卸载默认不保留用户数据，还会删除：

- `%LOCALAPPDATA%/PeripheralBattery` 中的配置、日志和临时查询文件。
- `%TEMP%/.net/PeripheralBattery` 中旧版的解包缓存，以及 `%TEMP%/.net/PeripheralBattery.Backend` 中新版查询 / 设置组件的缓存。
- 若设置了 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`，删除其下本应用的 `PeripheralBattery` 和 `PeripheralBattery.Backend` 子目录。

只清理这些应用专用目录，不删除整个临时目录、.NET 缓存根目录或其他软件的数据。用户自行保存到其他位置的诊断导出、下载的安装包及源码不会被删除。安装包默认不生成持久安装日志，开发测试通过显式 `/LOG` 生成的日志留在项目的 `evidence/` 中。

安装包为本项目自行构建，尚未代码签名。Release 提供安装程序和 SHA256SUMS.txt，不再生成便携 ZIP。应用验证统一通过安装程序覆盖升级，保留原目录和用户设置。

开发者构建：安装 [Inno Setup](https://jrsoftware.org/isdl.php)，然后运行 `./build.ps1 -Installer -Iscc 'ISCC.exe 的完整路径'`。安装脚本位于 `installer/PeripheralBattery.iss`，应用安装与卸载都采用当前用户模式；不会修改机器级环境变量或系统驱动。
