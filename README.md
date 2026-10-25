# CAN 下载器

基于 [Avalonia](https://avaloniaui.net/) 的 CAN 软件下载器：安装、更新 CAN 的各个客户端（CAN Pilot Client，Audio / ATIS / MSFS / XPC for CAN）。界面风格与 [can-client-ui](https://github.com/JianyueLab-Org/can-client-ui) 相同：深色、扁平、紧凑，同一套代码发布 Windows、macOS（仅 Apple Silicon ARM64）、Linux（通用 tar.gz）三个平台，软件库只列出有本机平台安装包的软件（目前 Audio / ATIS / MSFS / XPC for CAN 只有 Windows 版，CAN Pilot Client 三个平台都有）。

已接入 [CAN API](https://api.ceruleanavi.net)（源码 can-api）：账号登录、客户端发布信息、安装包下载、下载器自更新。

| 功能 | API |
|---|---|
| 登录（OAuth 2.1 授权码 + PKCE，公开客户端，在 [platform.ceruleanavi.net](https://platform.ceruleanavi.net/) 注册） | 发现文档 `/.well-known/openid-configuration`；授权页 `https://ceruleanavi.net/oauth/authorize`；`/api/oauth/token`、`/api/oauth/userinfo`、`/api/oauth/revoke` |
| 软件列表与版本 | `GET /api/v1/clients/latest`：`clients` 是 can-audio 的客户端合集；`apps` 是独立发布的程序（`can-pilot`、`can-downloader`），各自有版本、SHA-256 和发布说明 |
| 下载器自更新 | `apps.can-downloader`（下载地址同下） |
| 各平台安装包 | `clients.<名称>.platforms` / `apps.<名称>.platforms`（可选）：以平台标识为键（`win-x64`、`osx-arm64`、`osx-universal`、`linux-x64`、`linux-arm64`），值为 `{download, origin, size, sha256}`。没有时顶层地址是 Windows 版，见下面“平台” |
| 下载（下载源“本站”） | `GET /api/v1/clients/download/{client}`，支持 Range 续传 |
| 下载（下载源“GitHub”） | 发布信息里的 GitHub Releases 原始地址；私有仓库（CAN Pilot Client、下载器）没有公开地址，始终走本站 |

## 流程

**启动窗口 → 登录窗口 → 主界面**

| 界面 | 说明 |
|---|---|
| 启动窗口 | 打开后先检查下载器自身更新：已是最新则直接进入登录；有新版本时显示更新说明（来自 GitHub 发布说明），可“立即更新”或“稍后”；连接失败可重试或退出 |
| 登录窗口 | “使用 CAN 账号登录”：在浏览器中打开 CAN 网站登录并授权，下载器在 `127.0.0.1` 的随机端口接收回调（不接触密码）；“自动登录”时 refresh 令牌加密保存在本机（Windows 上用 DPAPI），下次启动直接登录；退出登录时吊销授权。没有离线模式，必须登录才能进入主界面 |
| 主窗口 | 工具栏（检查更新、全部更新、设置）、下载速度汇总、账号菜单（账号信息、退出登录）；提示条报告新上架的软件、可更新的软件、下载器新版本、检查失败 |
| 软件库 | 左侧按分类分组、可搜索；**软件来自 API 的发布信息**，API 新增客户端后点“检查更新”即自动出现，移除的已安装软件标记“已下架” |
| 软件详情 | 安装 / 更新 / 打开，下载进度（暂停、继续、取消；暂停后续传），简介、版本信息、更新说明链接；⋯ 菜单：打开目录、校验并修复（重新下载覆盖安装）、卸载 |
| 安装 | 安装位置（可浏览选择；设置里关闭“安装时询问安装位置”后不显示，直接装到默认目录）、可选组件、同时安装依赖项、所需与可用空间 |
| 设置 | 常规、下载（默认安装目录、是否询问安装位置、缓存、同时下载数、限速、校验）、更新（**下载源：本站 / GitHub**、检查频率、自动更新）、网络（代理、超时）。保存在 `data/settings.json` |

## 软件清单

清单由 `GET /api/v1/clients/latest` 生成（`src/Downloader/ViewModels/Manifest.cs`），显示信息来自 `src/Downloader/Assets/catalog.json`（复制到输出目录根）：

- `categories`：软件库分类（顺序即显示顺序）
- `products`：按 API 的客户端名称（如 `audio-for-can`）提供名称、分类、图标字母与颜色、简介、可执行文件

API 里有、catalog.json 里没有的客户端也会出现，归入“其他”。

## 平台

| 平台 | 下载器安装包 | 说明 |
|---|---|---|
| Windows x64 | `CAN-Downloader-<版本>-win-x64.zip` | 只含 `Downloader.exe`，便携 |
| macOS 12+（ARM64） | `CAN-Downloader-<版本>-osx-arm64.zip` | `CAN Downloader.app`，仅支持 Apple Silicon，包内直接使用 ARM64 单文件程序。ad-hoc 签名、没有公证：用浏览器下载的话第一次打开前执行 `xattr -dr com.apple.quarantine "/Applications/CAN Downloader.app"` |
| Linux（x64） | `CAN-Downloader-<版本>-linux-x64.tar.gz` | 解压后直接运行 `can-downloader/Downloader`，或 `./install.sh` 装到 `~/.local/share/can-downloader` 并加入应用菜单。依赖 glibc、ICU、fontconfig、X11 库（Wayland 下经 XWayland）和中文字体 |

下载器按本机平台挑安装包（`ViewModels/Manifest.cs`）：API 给出 `platforms` 时按优先顺序挑（macOS 优先 `osx-arm64`，兼容客户端的 `osx-universal` 通用包）；没有 `platforms` 时顶层地址是 Windows 版，macOS / Linux 按文件名约定把 GitHub 原始地址里的 `-win-x64.zip` 换成 `-osx-arm64.zip` / `-linux-x64.tar.gz`，只能从 GitHub 下载，SHA-256 从同一发布的 `SHA256SUMS` 查。都没有的软件不出现在软件库里。

安装软件：Windows 和 macOS 的安装包是 zip（macOS 里是 `.app`，整个包装进安装目录，“打开”用 `open`），Linux 是 tar.gz（保留可执行权限）；软件库里写的 `xxx.exe` 在 macOS / Linux 上是去掉 `.exe` 的程序名。

Linux 安装包同时提供 `install.sh` 和 `uninstall.sh`，默认安装到 `~/.local/bin/CAN/downloader/`，一个程序一个子文件夹。安装后可运行该目录的 `uninstall.sh`，选择是否清理下载器设置、日志和缓存；也支持 `--keep-data` / `--remove-data`。其他 CAN 客户端及已安装的软件不会删除，`install.sh --uninstall` 仍可使用。

macOS 安装包解压后包含 `CAN Downloader.app`、`install.sh` 和 `uninstall.sh`：

```bash
sh install.sh                                      # 安装到 /Applications/CAN/downloader
sh install.sh "$HOME/Applications/CAN/downloader"   # 安装到当前用户目录
sh uninstall.sh                                    # 卸载默认目录中的下载器
sh uninstall.sh "$HOME/Applications/CAN/downloader" # 卸载当前用户目录中的下载器
```

macOS 同样一个程序一个子文件夹。安装后也可运行 `.app/Contents/Resources/uninstall.sh`，自动定位应用所在目录。覆盖安装或卸载前请先退出下载器；卸载保留用户数据、已安装客户端及 CAN 下的其他程序。

上述 `uninstall.sh` 仅用于卸载 CAN Downloader，不用于卸载客户端。客户端通过下载器中的“卸载”功能、客户端自带的卸载器（如有）或手动删除其独立目录进行卸载；下载器中的“卸载”只删除该客户端记录的文件和对应安装记录，不影响其他客户端。

## 目录（便携）

所有目录默认都在下载器程序所在的目录下，整个目录复制走即可带走。程序目录不能写时放到用户目录：macOS 的 `.app` 包（`~/Library/Application Support/CAN Downloader`）、Linux 不可写目录（如 `/opt`，数据放 `~/.local/share/CAN Downloader`，或 `$XDG_DATA_HOME`）。Windows 始终在 exe 所在目录。

| 目录 | 内容 |
|---|---|
| `apps/<客户端名>` | 默认安装位置（设置里可改，相对路径按上面的目录计算） |
| `downloads/` | 安装包缓存；下载先写 `.part`，暂停或网络中断后用 Range 续传 |
| `data/` | `settings.json` 设置、`installs.json` 安装记录、`account.json` 自动登录选项与 refresh 令牌（Windows 上 DPAPI 加密；macOS / Linux 上目录 700、文件 600） |
| `logs/` | 日志，每天一个文件（`2026-10-23.log`）；记录启动、每个 API 请求、登录结果、下载 / 安装 / 卸载、错误。不记录令牌和授权码 |

不要把下载器放在 `Program Files` 这类需要管理员权限才能写入的目录。

## 安装过程

- 校验：发布信息带 SHA-256 时（`apps` 里的程序，或从 `SHA256SUMS` 查到时）总是核对；“下载完成后校验文件”另外完整读取压缩包中的每个文件
- 下载入队时固定版本、地址、大小、校验信息和安装位置；刷新清单不会改变正在下载、排队或暂停的任务。缓存按发布身份隔离
- 软件正在运行时不能更新或修复；开始任务和提交文件替换前都会按进程实际路径检查，需先关闭软件
- 先完整解压到暂存目录（zip 或 tar.gz），去掉与软件同名的顶层目录（macOS 的 `.app` 除外）；更新时备份受影响文件，再替换和移除旧版本多余文件
- 文件变更和安装记录一起提交；失败时恢复旧文件，进程中断后在下次加载安装记录前通过 `data/transactions/` 的日志恢复。恢复失败时保留备份供重试
- 卸载只删除安装记录里的文件，用户自己放进去的文件保留

## 运行

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet run --project src/Downloader
dotnet run --project src/Downloader -- --offline   # 演示无法连接 API（启动窗口显示失败与重试）
dotnet run --project tests/Downloader.RegressionTests -c Release # 本地 HTTP 下载与安装故障回归测试
```

## 自更新

替换的对象：Windows 是 `Downloader.exe`，Linux 是 `Downloader`，macOS 是整个 `CAN Downloader.app`。

1. 下载本平台的新版本，核对 SHA-256，把其中的程序（或 `.app`）解压为 `<原名>.new`
2. 以 `<原名>.new --apply-update <进程号>` 启动新版本（`.app` 启动包里的入口），当前进程退出
3. 新版本等旧进程退出，把自己复制到原位置（macOS 先把旧的 `.app` 改名为 `.old`，复制失败时还原）并启动它；最后启动的程序删除 `.new` / `.old`

正在运行的程序文件始终不动：单文件程序按需从程序文件读取程序集，运行中改名会导致之后加载失败。这套流程从 0.0.2 起生效（0.0.1 没有自更新）；编译输出目录里的开发版、程序目录不可写的版本不会自更新。

开发时可以用环境变量 `CAN_API_URL` 指向本地运行的 can-api（启动时会在日志里提示）。

## 发布

```bash
dotnet publish src/Downloader -p:PublishProfile=win-x64   # Windows：publish/win-x64/Downloader.exe
bash packaging/build.sh                                   # macOS ARM64：dist/ 下 .app 的 zip；Linux：dist/ 下 tar.gz
```

`packaging/build.sh` 也可指定目标（`osx-arm64`、`linux-x64`、`linux-arm64`）；macOS 版只能在 macOS 上打。各平台的发布配置在 `src/Downloader/Properties/PublishProfiles/`（共用 `SingleFile.props`），可以在 Windows 上 `dotnet publish src/Downloader -p:PublishProfile=linux-x64` 交叉编译单文件程序。推送 tag 后 CI（`.github/workflows/build.yml`）打出 Windows x64、macOS ARM64、Linux x64 的安装包和 `SHA256SUMS` 上传到 Release。

版本号只在 `src/Downloader/Downloader.csproj` 的 `<Version>` 里写一处，GitHub Release 的 tag 为 `v<版本号>`（0.x 是预发布；正式版从 3.0.0 起编号，之前发过的 1.0.0 是同一条线上的旧版本）。

每个平台都是一个单文件程序（约 49 MB）：自包含，不需要安装 .NET；Avalonia 的原生库也打包在内，首次运行时解压到临时目录。`catalog.json` 嵌入在程序里，程序旁边放一个 `catalog.json` 会优先使用。

## 技术栈

- .NET 10 / C#
- Avalonia 12.1（Simple 主题打底 + 自定义主题）
- CommunityToolkit.Mvvm

## 目录结构

```
src/Downloader/
├── App.axaml(.cs)          # 主题装配；启动窗口 → 登录 → 主界面 的窗口切换
├── Assets/catalog.json     # 软件库的分类与显示信息
├── Services/               # CAN API 客户端、下载与安装、本机数据、平台差异（Platform.cs）
├── Styles/
│   ├── Theme/
│   │   ├── Tokens.axaml        # 设计令牌：颜色、字体、尺寸、圆角
│   │   ├── ControlThemes.axaml # 按钮、标签页、列表项、导航项等控件主题
│   │   └── Controls.axaml      # 其余控件的尺寸与状态样式、通用文字样式
│   └── App.axaml           # 业务样式：启动窗口、提示条、软件库、下载进度
├── ViewModels/             # 清单、自更新与登录流程、软件状态与下载队列
└── Views/                  # 启动窗口、登录窗口、主窗口、软件详情、安装对话框、设置
packaging/
├── build.sh                # macOS / Linux 发布并打包
├── macos/                  # ARM64 .app（make-app.sh）
└── linux/                  # tar.gz、install.sh、应用菜单项
```

## 修改外观

所有颜色、字体、尺寸、圆角都集中在 `Styles/Theme/Tokens.axaml`，页面里不直接写颜色值。主题文件与 can-client-ui 各客户端是同一份的副本。

按钮样式类：`primary`（主操作）、`ghost`（无边框）、`danger`（悬停危险色）、`icon`（正方形）、`small`（小号）。文字样式类：`muted`、`caption`、`section`、`label`、`mono`。

## 状态

- [x] 自更新、按清单生成软件库、下载队列的界面
- [x] CAN 账号 OAuth 登录、自动登录（需要在 platform.ceruleanavi.net 注册下载器，把 client_id 填入 `Services/OAuthLogin.cs`）
- [x] 从 API 获取软件列表；真实下载（续传、限速、并发、本站 / GitHub 下载源）、校验与安装
- [x] 下载器自身的更新、CAN Pilot Client（需要 can-api 的 `apps` 部署上线，并配置能读取私有仓库的 `GITHUB_TOKEN`）
- [ ] can-voice 新一代客户端（`/api/v1/voice/*`，目前还没有发布可安装的构建）
- [x] 设置保存、便携目录、按日期分文件的日志、单文件发布
- [x] macOS（ARM64 .app）与 Linux（通用 tar.gz）版本；API 加上 `platforms` 后改为按平台的本站中转下载
