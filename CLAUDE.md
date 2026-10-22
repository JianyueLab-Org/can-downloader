# CLAUDE.md

本文件给在此仓库工作的 Claude 会话提供上下文。用户使用中文交流，界面文字也全部为中文。

## 项目概况

CAN 下载器的**界面原型**：Avalonia 12.1 + .NET 10，用来安装、更新 CAN 的各个客户端与数据。更新服务器、登录、下载、安装都是 ViewModel 里的模拟，尚未接入真实服务。界面风格与 `can-client-ui`（飞行员 / 管制员 / 通播客户端）一致，`Styles/Theme/*`、`TitleBar`、`DialogView`、`ResizeGrips` 是那边的副本（命名空间不同），改动通用外观时两边要同步。

```bash
dotnet build                                   # 通过 CanDownloader.slnx 编译，应当 0 警告 0 错误
dotnet run --project src/Downloader
dotnet run --project src/Downloader -- --offline   # 演示无法连接更新服务器
```

## 结构

| 界面 / 逻辑 | 文件 |
|---|---|
| 启动流程：**启动窗口 → 登录窗口 → 主界面**，窗口切换都在 `App.axaml.cs` | |
| 启动窗口：先检查下载器自身更新（检查中 → 已是最新 / 发现新版本 → 下载 → 安装并重启；失败时 重试 / 离线模式） | `Views/SplashWindow.axaml`，`ViewModels/SelfUpdateViewModel.cs` |
| 登录窗口：**登录后才能进入主界面**；启动时选了“稍后”的话这里还能更新下载器；也可以选离线模式（不登录，只能看缓存的软件列表，不能下载）。主界面离线时工具栏有“登录”，登录后右上角账号菜单可退出登录 | `Views/LoginWindow.axaml`，`ViewModels/LoginViewModel.cs` |
| 更新清单：下载器最新版本、分类、软件（版本、组件、依赖、更新日志）。**软件库完全由清单生成**，清单新增条目即自动出现，不要在代码里写死软件列表 | `ViewModels/Manifest.cs`，演示数据 `Assets/manifest.json`（复制到输出目录根，修改后点“检查更新”即生效） |
| 主界面：工具栏（检查更新、全部更新、设置、⋯、账号）、提示条（新上架、可更新、下载器新版本、离线）、左侧按分类分组的软件库、右侧软件详情与下载进度 | `Views/MainWindow.axaml`、`Views/ProductView.axaml`，`ViewModels/MainWindowViewModel.cs`、`ProductViewModel.cs` |
| 安装对话框（位置、组件、依赖、磁盘空间）、设置（常规 / 下载 / 更新 / 网络） | `Views/InstallView.axaml`、`Views/SettingsWindow.axaml` + `Views/Settings/*.axaml` |

本机已安装版本是 `LocalInstalls` 里的演示数据；下载、校验、安装由 `DispatcherTimer` 模拟，遵守设置里的同时下载数与限速。

## 设计要求（与 can-client-ui 一致，不要回退）

- **风格**：深色、极简、扁平、紧凑的开发者工具风格；不要 Fluent / Material 观感；不用渐变、玻璃拟态、夸张阴影；跨平台。
- **不使用 FluentAvalonia**，底层是 Avalonia 自带的 `SimpleTheme`。
- **字号小**：基础 12，控件高 26。
- **窗口**：无系统标题栏（`WindowDecorations="None"`），拖动工具栏空白处移动；主窗口右上角 — / ✕，边缘由 `ResizeGrips` 缩放；没有底部状态栏。子窗口用 `Views/TitleBar` 自绘标题栏；打开设置时主窗口蒙上暗色遮罩。
- 设置界面：左侧菜单 + 右侧面板 + 底部 确定/应用/取消，“应用”只在有改动时可用。

## 主题结构（修改外观的唯一入口）

- `Styles/Theme/Tokens.axaml`：所有颜色、字体、尺寸、间距、圆角。**页面里不要硬编码颜色**，一律 `{DynamicResource Brush.*}`。业务语义色是 `Brush.State.*`（已安装 / 可更新 / 进行中 / 失败）和 `Brush.Speed`。软件图标块颜色由清单提供，属于数据。
- `Styles/Theme/ControlThemes.axaml`、`Controls.axaml`：控件主题与通用样式（与 can-client-ui 相同）。
- `Styles/App.axaml`：启动窗口、登录窗口、提示条、软件库、软件详情、下载进度等业务样式。

## 代码约定

- MVVM，CommunityToolkit.Mvvm（`[ObservableProperty]`、`[RelayCommand]`）；启用编译绑定，XAML 必须写 `x:DataType`。
- 对话框用自绘的 `DialogView`，通过 `MainWindow.ShowDialogAsync(...)` 显示在窗口内遮罩层上；不要用系统对话框。
- 注释和界面文字使用中文。

## 踩过的坑（Avalonia 12）

- `TextBox` 占位符属性是 `PlaceholderText`；窗口装饰是 `WindowDecorations` 枚举。
- 一个 Style 选择器里混用不同类型设置 `FontFamily` 会编译失败，要拆开。
- `TextBlock` 里多个 `<Run>` 之间的换行会产生空格，写在同一行。
- 按钮、标签头字符串里的下划线会被当成快捷键吞掉。
- 清单文件要用 `Link="manifest.json"` 复制到输出目录根，否则会落在 `Assets/` 下读不到。

## 验证方式

没有自动化测试。改界面后编译到单独目录（`dotnet build -o <临时目录>`）另开实例，用 PowerShell UI Automation 点击按钮、`PrintWindow` 截图确认；不要关闭或干扰用户正在运行的窗口。

## Git

- 远程：`https://github.com/JianyueLab-Org/can-downloader`，分支 `main`。
- 本机 Clash 代理（127.0.0.1:7897）连 GitHub 时 TLS 会失败，推送时绕过代理：`git -c http.proxy= push`。
- 只在用户要求时提交或推送。
