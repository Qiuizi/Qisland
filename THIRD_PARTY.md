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
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 动效节奏参考（v4 动效）：形体弹簧参数（OPEN_W k330 ζ0.70、OPEN_H k290 ζ0.74、CLOSE k620 ζ1.0、SWELL k420 ζ0.8）、悬停胀大、收起越过停靠尺寸即贴住、内容按形体进度 smoothstep 揭开、翻页旧页残影 + 余速延续、页码点伸缩、铃铛摆动曲线 sin(4πt)·√(1−t)、系统「动画效果」开关；C# / WPF 自行实现（解析解弹簧），未复制代码 | MIT |
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

## 天气、组件与健康（v4 第三阶段）

| 项目 / 服务 | 用途 | 许可证 / 说明 |
| --- | --- | --- |
| Open-Meteo（api.open-meteo.com / geocoding-api.open-meteo.com） | 天气主数据源：实况、48 小时逐时、7 天逐日（降水概率、紫外线、日出日落、风向、气压、能见度），城市搜索；15 分钟刷新一次 | 免费非商业 API，数据 CC BY 4.0，卡片上标注「Open-Meteo」 |
| wttr.in | 自动定位时的地名、坐标与月相（Open-Meteo 不可用时兜底） | 公开免费服务 |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 思路参考：Open-Meteo 7 天预报与 15 分钟刷新、多城市（最多 5 个 + 自动定位）与岛上切换、中文城市名搜索规则（去「市/县/区/省」、省份查省会、两字名补「市」、跳过机场车站等地物、按精确匹配和人口排序、0.05° 去重）、天气图标动效（太阳转、雨雪落、闪电闪，进页面 / 悬停时短暂播放后静止）、数据过旧提示；健康页的喝水（250 ml 一杯、2000 ml 目标、45 分钟、9–22 点、午休静默、按小时柱状）与久坐提醒（连续 60 分钟）；剪贴板置顶；OLED 像素位移。全部 C# / WPF 自行实现，未复制代码。省会对照表为自行整理；天气提醒规则（快下雨 / 雷阵雨 / 下雪 / 大风 / 高温 / 严寒 / 大降温）为 Qisland 自有 | MIT |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 思路参考（v4 第四阶段）：待办提醒与每周重复、本地日历编辑器、统一设置窗、左右侧边停靠（竖向收起胶囊、面板从边缘滑出）与极简小条。C# / WPF 自行实现，未复制代码；待办提醒以 ⏰ / 🔁 写在 todo.md 行内为 Qisland 自有格式 | MIT |
| 岛仔 Daozai（Renly1994/xiaotang-daozai） | 思路参考：液态玻璃的系统合成模糊底板（单独的无重定向位图置顶窗口 + Windows.UI.Composition 宿主背景画刷，按岛的圆角裁剪、紧贴在岛下面；见其 composition.rs）。C# 自行实现，未复制代码 | MIT |
| vitrio（eamonliu/vitrio）、liquid-glass（archisvaze/liquid-glass）、OpenGlass | 思路参考：液态玻璃皮肤的观感拆解（中间通透、边缘折射带、外沿镜面描边 + 内圈斜面亮边、光源方向的高光）。用 Windows 合成视觉和 WPF 画刷自行近似，未复制代码 | MIT |

