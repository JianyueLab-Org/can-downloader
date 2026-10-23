# CAN 下载器

基于 [Avalonia](https://avaloniaui.net/) 的 CAN 软件下载器：安装、更新 CAN 的各个客户端（CAN Pilot Client，Audio / ATIS / MSFS / XPC for CAN）。界面风格与 [can-client-ui](https://github.com/JianyueLab-Org/can-client-ui) 相同：深色、扁平、紧凑，可在 Windows / macOS / Linux 上运行（目前发布的客户端都是 Windows 版）。

已接入 [CAN API](https://api.ceruleanavi.net)（源码 can-api）：账号登录、客户端发布信息、安装包下载、下载器自更新。

| 功能 | API |
|---|---|
| 密码登录（CAN ID 或邮箱） | `POST /api/v1/auth/signin` |
| 邮箱验证码登录 | `POST /api/v1/auth/code/request`、`/api/v1/auth/code/verify` |
| 自动登录时校验会话 / 退出登录 | `GET /api/v1/auth/session`、`POST /api/v1/auth/signout` |
| 软件列表与版本 | `GET /api/v1/clients/latest`：`clients` 是 can-audio 的客户端合集；`apps` 是独立发布的程序（`can-pilot`、`can-downloader`），各自有版本、SHA-256 和发布说明 |
| 下载器自更新 | `apps.can-downloader`（下载地址同下） |
| 下载（下载源“本站”） | `GET /api/v1/clients/download/{client}`，支持 Range 续传 |
| 下载（下载源“GitHub”） | 发布信息里的 GitHub Releases 原始地址；私有仓库（CAN Pilot Client、下载器）没有公开地址，始终走本站 |

## 流程

**启动窗口 → 登录窗口 → 主界面**

| 界面 | 说明 |
|---|---|
| 启动窗口 | 打开后先检查下载器自身更新：已是最新则直接进入登录；有新版本时显示更新说明（来自 GitHub 发布说明），可“立即更新”或“稍后”；连接失败可重试或离线模式进入 |
| 登录窗口 | CAN ID 或邮箱 + 密码，或邮箱验证码；记住账号、自动登录（会话保存在本机，Windows 上用 DPAPI 加密）；注册账号、忘记密码在浏览器中打开 ceruleanavi.net。登录后才能进入主界面下载；也可选“离线模式”，不登录进入，只能查看上次缓存的软件列表 |
| 主窗口 | 工具栏（检查更新、全部更新、设置；离线时为“登录”）、下载速度汇总、账号菜单（账号信息、退出登录）；提示条报告新上架的软件、可更新的软件、下载器新版本、离线模式 |
| 软件库 | 左侧按分类分组、可搜索；**软件来自 API 的发布信息**，API 新增客户端后点“检查更新”即自动出现，移除的已安装软件标记“已下架” |
| 软件详情 | 安装 / 更新 / 打开，下载进度（暂停、继续、取消；暂停后续传），简介、版本信息、更新说明链接；⋯ 菜单：打开目录、校验并修复（重新下载覆盖安装）、卸载 |
| 安装 | 安装位置（可浏览选择；设置里关闭“安装时询问安装位置”后不显示，直接装到默认目录）、可选组件、同时安装依赖项、所需与可用空间 |
| 设置 | 常规、下载（默认安装目录、是否询问安装位置、缓存、同时下载数、限速、校验）、更新（**下载源：本站 / GitHub**、检查频率、自动更新）、网络（代理、超时）。保存在 `data/settings.json` |

## 软件清单

清单由 `GET /api/v1/clients/latest` 生成（`src/Downloader/ViewModels/Manifest.cs`），显示信息来自 `src/Downloader/Assets/catalog.json`（复制到输出目录根）：

- `categories`：软件库分类（顺序即显示顺序）
- `products`：按 API 的客户端名称（如 `audio-for-can`）提供名称、分类、图标字母与颜色、简介、可执行文件

API 里有、catalog.json 里没有的客户端也会出现，归入“其他”。

## 目录（便携）

所有目录默认都在下载器 exe 所在的目录下，整个目录复制走即可带走：

| 目录 | 内容 |
|---|---|
| `apps/<客户端名>` | 默认安装位置（设置里可改，相对路径按 exe 目录计算） |
| `downloads/` | 安装包缓存；下载先写 `.part`，暂停或网络中断后用 Range 续传 |
| `data/` | `settings.json` 设置、`installs.json` 安装记录、`account.json` 记住的账号与自动登录会话、`manifest-cache.json` 离线模式用的软件列表 |
| `logs/` | 日志，每天一个文件（`2026-10-23.log`）；记录启动、每个 API 请求、登录结果、下载 / 安装 / 卸载、错误。不记录密码、验证码和会话内容 |

不要把下载器放在 `Program Files` 这类需要管理员权限才能写入的目录。

## 安装过程

- 校验：发布信息带 SHA-256 时（`apps` 里的程序）总是核对；“下载完成后校验文件”另外完整读取 zip 中的每个文件
- 解压到安装目录，去掉 zip 里与软件同名的顶层目录；更新时删除旧版本多出来的文件
- 卸载只删除安装记录里的文件，用户自己放进去的文件保留

## 运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/Downloader
dotnet run --project src/Downloader -- --offline   # 演示无法连接 API
```

## 自更新

1. 下载新版本的 zip，核对 SHA-256，把其中的 `Downloader.exe` 解压为 `Downloader.exe.new`
2. 以 `Downloader.exe.new --apply-update <进程号>` 启动新版本，当前进程退出
3. 新版本等旧进程退出，把自己复制到 `Downloader.exe` 并启动它；最后启动的程序删除 `.new`

正在运行的 exe 文件始终不动：单文件程序按需从 exe 读取程序集，运行中改名会导致之后加载失败。这套流程从 0.0.2 起生效（0.0.1 没有自更新）；编译输出目录里的开发版不会自更新。

开发时可以用环境变量 `CAN_API_URL` 指向本地运行的 can-api（启动时会在日志里提示）。

## 发布

```bash
dotnet publish src/Downloader -p:PublishProfile=win-x64
```

版本号只在 `src/Downloader/Downloader.csproj` 的 `<Version>` 里写一处，GitHub Release 的 tag 为 `v<版本号>`（0.x 是预发布；正式版从 3.0.0 起编号，之前发过的 1.0.0 是同一条线上的旧版本）。

输出 `publish/win-x64/Downloader.exe` 一个文件（约 49 MB）：自包含，不需要安装 .NET；Avalonia 的原生库也打包在内，首次运行时解压到临时目录。`catalog.json` 嵌入在 exe 里，exe 旁边放一个 `catalog.json` 会优先使用。

## 技术栈

- .NET 10 / C#
- Avalonia 12.1（Simple 主题打底 + 自定义主题）
- CommunityToolkit.Mvvm

## 目录结构

```
src/Downloader/
├── App.axaml(.cs)          # 主题装配；启动窗口 → 登录 → 主界面 的窗口切换
├── Assets/catalog.json     # 软件库的分类与显示信息
├── Services/               # CAN API 客户端、下载与安装、本机数据
├── Styles/
│   ├── Theme/
│   │   ├── Tokens.axaml        # 设计令牌：颜色、字体、尺寸、圆角
│   │   ├── ControlThemes.axaml # 按钮、标签页、列表项、导航项等控件主题
│   │   └── Controls.axaml      # 其余控件的尺寸与状态样式、通用文字样式
│   └── App.axaml           # 业务样式：启动窗口、提示条、软件库、下载进度
├── ViewModels/             # 清单、自更新与登录流程、软件状态与下载队列
└── Views/                  # 启动窗口、登录窗口、主窗口、软件详情、安装对话框、设置
```

## 修改外观

所有颜色、字体、尺寸、圆角都集中在 `Styles/Theme/Tokens.axaml`，页面里不直接写颜色值。主题文件与 can-client-ui 各客户端是同一份的副本。

按钮样式类：`primary`（主操作）、`ghost`（无边框）、`danger`（悬停危险色）、`icon`（正方形）、`small`（小号）。文字样式类：`muted`、`caption`、`section`、`label`、`mono`。

## 状态

- [x] 自更新、登录 / 离线模式、按清单生成软件库、下载队列的界面
- [x] CAN 账号登录（密码 / 邮箱验证码、自动登录）
- [x] 从 API 获取软件列表；真实下载（续传、限速、并发、本站 / GitHub 下载源）、校验与安装
- [x] 下载器自身的更新、CAN Pilot Client（需要 can-api 的 `apps` 部署上线，并配置能读取私有仓库的 `GITHUB_TOKEN`）
- [ ] can-voice 新一代客户端（`/api/v1/voice/*`，目前还没有发布可安装的构建）
- [x] 设置保存、便携目录、按日期分文件的日志、单文件发布
