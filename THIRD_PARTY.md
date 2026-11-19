# THIRD_PARTY.md — 第三方组件与许可证

WinIslands 应用本体为 **MIT License**（见 [LICENSE](LICENSE)）。

运行时（发布产物）不依赖任何第三方 NuGet 包，仅使用 .NET 平台自带组件：

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| .NET 8 Runtime / SDK（Microsoft.NETCore.App, Microsoft.WindowsDesktop.App） | 运行时、WPF、WinForms | MIT |
| Windows SDK 投影 `Microsoft.Windows.SDK.NET.Ref`（CsWinRT） | `Windows.Media.Control`（SMTC）等 WinRT API 互操作 | MIT |
| WPF（WindowsBase, PresentationFramework, PresentationCore） | 界面框架 | MIT |
| Windows Forms（System.Windows.Forms, System.Drawing） | 托盘图标 NotifyIcon、文件夹对话框 | MIT |
| `System.Text.Json`（.NET 内置） | 配置文件序列化 | MIT |
| `System.Net.Http`（.NET 内置） | Cider API / 封面下载 / 在线歌词 | MIT |

> 说明：上述均为 .NET 平台自带组件，随 .NET SDK/Runtime 分发，许可证为 MIT（.NET 使用 MIT，部分组件含 Apache-2.0 头部，详见 .NET 仓库 LICENSE）。

## 开发/测试依赖

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| xUnit.net v3 (`xunit.v3`) | 单元测试 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 测试宿主 | MIT |

## 图标与资源
- 应用图标与截图：WinIslands 原创（MIT）。

## 未使用
- 无遥测 / 广告 / 商业组件。

## UI 设计参考 / 移植

| 项目 | 用途 | 许可证 |
| --- | --- | --- |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 岛体布局、纯黑/液态玻璃配色、字体层级、页标题、圆形徽标、页码豆与组件视觉语言；WPF 端按其设计常量重实现 | MIT |
| LiquidMusic（JudeKwong） | 歌词滚动 ease-out cubic tween、逐行焦点缩放/透明度、程序坞液态玻璃配方（半透明底 + 1px 内高光 + 柔和投影）；WPF 端按相同参数重实现 | 同作者项目，随本仓库分发 |

## 在线数据源 / 思路参考（v4）

| 项目 | 用途 | 许可证 |
| --- | --- | --- |
| LRCLIB（lrclib.net） | 非网易云播放器的同步歌词：按歌名 + 歌手搜索，本地按时长（≤ 3 秒）和版本过滤，结果缓存在本机，低频请求 | 公开免费 API，歌词内容版权归原权利人 |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 思路参考：Apple Music 的「歌手 — 专辑」字段拆分、歌词候选时长约束与版本过滤、迟到歌词作废、按歌记住的歌词偏移；C# 自行实现，未复制代码 | MIT |

## 翻译、AI 与本机缓存（v4 第二阶段）

| 项目 / 服务 | 用途 | 许可证 / 说明 |
| --- | --- | --- |
| MyMemory（api.mymemory.translated.net） | 默认免 Key 翻译，只在你按快捷键或点「译」时发送选中的文字 | 公开免费 API，有每日额度 |
| 豆包（火山方舟）/ DeepSeek / Kimi（Moonshot）/ 通义（百炼兼容模式）/ Ollama | 可选翻译与问 AI 引擎，用你自己的 Key（Ollama 为本机） | 各自服务条款；Qisland 只调用 OpenAI 兼容的 chat/completions 与官方余额接口 |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 思路参考：豆包网页额度接口与字段含义（window_limit_groups、window_type、used_percent、end_time、710012001 登录失效）、Cookie 中 sessionid 的校验规则；酷狗 KRC 缓存目录（KuGou.ini 的 LyricPath）与文件名格式；汽水音乐 LunaCacheV2（LMDB + MessagePack）里歌词字段的位置。C# 自行实现，未复制代码 | MIT |
| KRC 格式公开资料（krc1 头 + 16 字节异或表 + zlib） | 解码酷狗本机歌词缓存 | 公开格式说明，自行实现 |
| Gemini CLI / Qwen Code / Cursor / OpenCode 的 hooks / 插件文档 | 接入格式（只在你逐个同意后写入） | 各自文档 |
