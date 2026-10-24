# CLAUDE.md

本文件给在此仓库工作的 Claude 会话提供上下文。用户使用中文交流，界面文字也全部为中文。

## 项目概况

CAN 下载器：Avalonia 12.1 + .NET 10，用来安装、更新 CAN 的各个客户端。已接入 CAN API（线上 https://api.ceruleanavi.net，源码在 `G:\can-api-main`）：登录、软件列表、下载、下载器自更新都是真实的。界面风格与 `can-client-ui`（飞行员 / 管制员 / 通播客户端）一致，`Styles/Theme/*`、`TitleBar`、`DialogView`、`ResizeGrips` 是那边的副本（命名空间不同），改动通用外观时两边要同步。

```bash
dotnet build                                   # 通过 CanDownloader.slnx 编译，应当 0 警告 0 错误
dotnet run --project src/Downloader
dotnet run --project src/Downloader -- --offline   # 演示无法连接 API（启动窗口失败 → 重试 / 退出）
dotnet publish src/Downloader -p:PublishProfile=win-x64   # 单文件 publish/win-x64/Downloader.exe（自包含）
```

## 结构

| 界面 / 逻辑 | 文件 |
|---|---|
| 启动流程：**启动窗口 → 登录窗口 → 主界面**，窗口切换都在 `App.axaml.cs` | |
| 启动窗口：先检查下载器自身更新（检查中 → 已是最新 / 发现新版本 → 下载 → 安装并重启；失败时 重试 / 退出） | `Views/SplashWindow.axaml`，`ViewModels/SelfUpdateViewModel.cs` |
| 登录窗口：**登录后才能进入主界面，没有离线模式**。OAuth 2.1 授权码 + PKCE（公开客户端，在 https://platform.ceruleanavi.net 注册，client_id 是 `OAuthLogin.RegisteredClientId`，环境变量 `CAN_OAUTH_CLIENT_ID` 可覆盖）：浏览器打开授权页，`127.0.0.1` 随机端口收回调（注册的回调地址 `http://127.0.0.1/callback`，端口不参与匹配），scope `openid profile email offline_access`；“自动登录”保存 DPAPI 加密的 refresh 令牌（每次刷新都换新，要保存新的）；退出登录时吊销。下载器不接触密码 | `Views/LoginWindow.axaml`，`ViewModels/LoginViewModel.cs`，`Services/OAuthLogin.cs` |
| CAN API 客户端（错误码转中文、代理与超时取自设置；OAuth 请求经 `SendRawAsync`） | `Services/CanApi.cs`、`Services/ApiModels.cs` |
| 软件清单：由 `GET /api/v1/clients/latest` 生成——`clients`（can-audio 合集，同一版本）加 `apps`（独立发布的 can-pilot 等，各自版本、SHA-256、发布说明）；`apps.can-downloader` 是下载器自己，用于自更新，不进软件库。显示信息（分类、名称、图标、简介）来自 `Assets/catalog.json`。**软件库完全由 API 决定**，不要在代码里写死软件列表 | `ViewModels/Manifest.cs`，`Assets/catalog.json` |
| 自更新：下载 → 核对 SHA-256 → 解压为 `Downloader.exe.new` → 以 `--apply-update <pid>` 启动它并退出 → 新版本复制自己到原位置再启动。**不要在运行中给 exe 改名后继续运行**（单文件程序之后加载程序集会失败） | `Services/SelfUpdater.cs`、`ViewModels/SelfUpdateViewModel.cs` |
| 下载（续传、限速、本站 / GitHub 下载源）、zip 校验、解压安装、卸载 | `Services/PackageInstaller.cs` |
| **目录都在 exe 所在目录下**（便携）：`data/`（设置、安装记录、自动登录的 refresh 令牌）、`logs/`（每天一个文件）、`downloads/`、`apps/`（默认安装目录）。不要改回 %LocalAppData% | `Services/LocalData.cs`（`AppPaths`）、`Services/Log.cs` |
| 安装位置：设置里的默认安装目录 + 安装对话框里询问（设置可关闭“安装时询问安装位置”，关闭后没有其他可选项时不弹对话框） | `Views/InstallView.axaml`、`ViewModels/InstallViewModel.cs`、`Views/FolderPicker.cs` |
| 主界面：工具栏（检查更新、全部更新、设置、⋯、账号）、提示条（新上架、可更新、下载器新版本、检查失败）、左侧按分类分组的软件库、右侧软件详情与下载进度 | `Views/MainWindow.axaml`、`Views/ProductView.axaml`，`ViewModels/MainWindowViewModel.cs`、`ProductViewModel.cs` |
| 安装对话框（位置、组件、依赖、磁盘空间）、设置（常规 / 下载 / 更新 / 网络） | `Views/InstallView.axaml`、`Views/SettingsWindow.axaml` + `Views/Settings/*.axaml` |

下载在后台线程进行，`MainWindowViewModel` 用 `DispatcherTimer` 把进度刷新到界面；遵守设置里的同时下载数、限速与下载源。设置保存在 `data/settings.json`（`SettingsViewModel` 所有可写公共属性），程序目录内的路径存相对路径；设置窗口“取消”/✕ 会还原。

发布是**单文件**（`Properties/PublishProfiles/win-x64.pubxml`）：新增数据文件要嵌入 exe（`EmbeddedResource`），不要依赖 exe 旁边的文件。

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
- `catalog.json` 既嵌入 exe（`LogicalName="catalog.json"`），也用 `Link="catalog.json"` 复制到编译输出目录根（方便修改，发布时不复制）。
- 单文件发布时 SkiaSharp 包会带出 100 MB 的原生 .pdb，发布配置里已排除。
- PowerShell 5 读不带 BOM 的 UTF-8 脚本会把中文弄乱；UI Automation 脚本里有中文时存成带 BOM 的文件。
- can-api 的登录接口要求 `Content-Type: application/json`，否则返回 415。
- 联调未部署的 can-api：设环境变量 `CAN_API_URL=http://127.0.0.1:端口` 启动下载器。

## 验证方式

没有自动化测试。不要用真实账号测试登录（线上服务）；OAuth 的本机回调可以用不打开浏览器的方式测试（`OAuthLogin.SignInAsync` 的 openBrowser 参数）。改界面后编译到单独目录（`dotnet build -o <临时目录>`）另开实例，用 PowerShell UI Automation 点击按钮、`PrintWindow` 截图确认；不要关闭或干扰用户正在运行的窗口。

## Git

- 远程：`https://github.com/JianyueLab-Org/can-downloader`，分支 `main`。
- 本机 Clash 代理（127.0.0.1:7897）连 GitHub 时 TLS 会失败，推送时绕过代理：`git -c http.proxy= push`。
- 只在用户要求时提交或推送。
- CI：`.github/workflows/build.yml`。推送 main / PR 只编译；推送 tag `v<版本号>`（必须与 `Downloader.csproj` 的 `<Version>` 一致，否则失败）时单文件发布，打包 `CAN-Downloader-<版本号>-win-x64.zip`（只含 `Downloader.exe`）上传到同名 Release。Release 已存在时只覆盖安装包、不改发布说明（发布说明会显示为下载器的更新说明）；0.x 标为预发布，其余标为正式版、最新版。正式版号从 3.0.0 起（曾发过 1.0.0），下一个版本在此基础上递增。
