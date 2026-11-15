# DeepSeek Harness

这台机器上的 DeepSeek Harness 是杭州深度求索的 Electron 桌面应用，版本 0.2.2-rc.1，不是命令行。

- 程序：`D:\Program Files (x86)\DSH\DeepSeek Harness.exe`
- 用户数据：`%APPDATA%\@deepseek-ai\dsh-desktop`
- 里面有 Chromium 配置、IndexedDB、Local Storage、`logs\`、`desktop\settings.json`
- `plugin-operations\operations.jsonl` 只是插件安装日志（例如 `@deepseek-ai/dsh-experimental-auto-review` 被 enable），不是会话事件

Qisland **不读** IndexedDB / LevelDB。额度如果只写在那里，岛上就不会显示额度。

## 实际在用的两条

1. **进程和主窗口标题。** 每几秒看一次进程名里同时有 DeepSeek 和 Harness 的进程，读 `MainWindowTitle`。标题变化会更新同一张卡片。
   - 出现「等待确认 / 批准 / approval」→ 等待确认
   - 出现「失败 / error」→ 出错
   - 出现「已完成 / completed」→ 完成，很快收回
   - 进程还在、标题就是应用名 → 进行中
   - 进程没了 → 这张卡片去掉
2. **系统通知。** 和微信、QQ 走同一个 `UserNotificationListener`。应用名或 AUMID 里有 `DeepSeek Harness` 或 `dsh-desktop` 才收。通知文案用同一套关键词。隐私模式打开时，窗口标题和通知正文都换成「等待确认 / 进行中 / 已完成」，不把原文放上岛。

设置里可以单独关掉 DeepSeek Harness，也可以和全部 AI 助手一起关。

## 可选插件草稿

公开文档在 [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)：插件是 Cordis 模块，导出 `name` 和 `apply(ctx)`，用 `ctx.on` 听事件，用 `ctx.effect` 管清理。生命周期文档里，轮次状态是 `agent/status`（`running` / `idle`），一轮自然结束前会过 `agent/turn-stopping`。实验性的 auto-review 插件还听了 `tools/pre-execute`，并且 `inject` 了 `approval`。

`plugins/dsh-qisland/` 是按这些文档写的草稿。它只旁路 POST，不拦截工具，也不改批准结果。桌面版 0.2.2-rc.1 的插件安装走的是应用自己的插件页 / `dsh plugin`，这份草稿没有在那台已安装的应用里跑过。进程和通知不依赖它。

装的时候不要把草稿拷进 `app.asar`。如果应用认本地插件目录，再按它当时的说明加进去。源码树里的装法长这样，仅供对照：

```sh
dsh plugin --profile web add ./plugins/dsh-qisland
```

这条命令是否被已安装的桌面版接受，要以那一版的插件页为准。接不上就继续用进程和通知。
