# Qisland

给自己用的 Windows 10/11 灵动岛。界面是一条连续的胶囊，在紧凑、展开和多条活动之间用弹簧动画过渡。内容靠间距和字重分开，不靠分割线。

它是在 [WinIslands](https://github.com/JudeKwong/WinIslands) v3.1.2（JudeKwong，MIT）上继续做的，没有把原来的上岛 API、SMTC 媒体和进程外插件换掉。本仓库保留同一份 MIT 许可证。

## 岛上会显示什么

| 来源 | 现在怎么接 |
|---|---|
| Claude Code、Codex | 官方 hook 把事件 POST 到本机 API |
| DeepSeek Harness 桌面版 | 看进程和主窗口标题，并镜像它的系统通知。不读 LevelDB |
| 信息站 Info Station | 轮询它自己的本地 HTTP API |
| CourseDesk | 读它写出的快照，或让它直接推送。补丁在 `docs/coursedesk-patch.md` |
| JARVIS | 只定义了推送约定，等那边以后来调 |
| 微信、QQ | 系统通知镜像，可按应用开关，可隐藏内容 |
| 天气、计时、待办 | Open-Meteo（默认成都）、原有番茄钟、本地 JSON 待办 |
| 媒体 | 原来的 SMTC / Cider |

多条活动同时出现时，最重要的一条留在胶囊里，其余收成右侧的小圆点，悬停能看到标题。

## 在 Windows 上构建和运行

需要 .NET 8 SDK。

```powershell
git clone <这个仓库>
cd <仓库>
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
.\dist\Qisland.exe
```

`build.ps1` 成功时打印 `BUILD_OK`。`publish.ps1` 打出一个自包含的 win-x64 目录 `dist\`，成功时打印 `PUBLISH_OK`。

第一次启动会把天气城市设为成都，并让岛在没有音乐时也留在屏幕上。设置在 `%APPDATA%\Qisland\settings.json`。托盘图标里可以打开设置。来源页能单独关掉每一类。

上岛 API 默认开着：`http://127.0.0.1:9840`。协议见 [docs/push-api.md](docs/push-api.md)。

## 接上三个编程助手

在 Windows 上：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-claude-hooks.ps1
powershell -ExecutionPolicy Bypass -File scripts\install-codex-hooks.ps1
```

Claude Code 写的是 `%USERPROFILE%\.claude\settings.json` 里的 hooks。Codex 写的是 `%USERPROFILE%\.codex\config.toml` 的 `notify`。如果 Codex 里已经有 `notify`，脚本不会覆盖，会把要加的命令打印出来。

DeepSeek Harness 是安装在本机的 Electron 应用（`DeepSeek Harness.exe`），不是命令行。不要给它装 CLI hook。Qisland 会自己看进程和通知。可选的 Cordis 插件草稿在 `plugins/dsh-qisland/`，说明见 [docs/deepseek-harness.md](docs/deepseek-harness.md)。

助手如果跑在 WSL2 Ubuntu 里，用 `scripts/qisland-push`。镜像网络可以直接打 `127.0.0.1`。NAT 模式打不到只监听回环的 API，脚本会改把 JSON 写到 Windows 的收件箱。两种模式都写在 [docs/push-api.md](docs/push-api.md) 里。

## 信息站和 CourseDesk

信息站后端默认 `http://127.0.0.1:8765`。Qisland 读高分未读、今日简报和临近截止。如果设了 `INFO_STATION_TOKEN`，把同一个值填进设置里的 Token。可选的推送补丁在 [docs/info-station.md](docs/info-station.md)。

CourseDesk 目前没有本地 HTTP。补丁让它在通知和快照变化时推过来，见 [docs/coursedesk-patch.md](docs/coursedesk-patch.md)。

## 开发

提供方的解析在 `src/Qisland.Core`，不依赖 WPF，Linux 上也能测：

```bash
dotnet test tests/Qisland.Core.Tests/Qisland.Core.Tests.csproj
dotnet build Qisland.sln -p:EnableWindowsTargeting=true
```

界面工程仍是原来的 `src/WinIslands`（程序集名叫 Qisland）。WPF 窗口要在 Windows 上看。

## 许可

MIT。上游版权属于 WinIslands 的贡献者，个人改动属于 Qiuizi。见 `LICENSE`。
