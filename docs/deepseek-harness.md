# DeepSeek Harness

DeepSeek Harness 是深度求索的 Electron 桌面应用（测试时版本 0.2.2-rc.1），不是命令行。

- 程序：`<安装目录>\DeepSeek Harness.exe`
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

## 在岛上批准：dsh-qisland-bridge 插件

DSH 的审批既不弹系统通知，也不改窗口标题，所以上面两条接不住它。要在岛上处理审批，得装 [integrations/dsh-qisland-bridge](../integrations/dsh-qisland-bridge) 这个 Cordis 插件。

它挂在 `approval/request` 上，处理方式和 [CodeIsland](https://github.com/wxtsky/CodeIsland) 的 DSH 桥 [dsh-codeisland-bridge](https://github.com/LeLiu/dsh-codeisland-bridge) 一样，岛先接审批，接不住再交回 DSH：

- 读取 Qisland 启动时生成的 `%APPDATA%\Qisland\approval.key`（只允许当前 Windows 用户访问），把工具名和命令签名后发到 `POST http://127.0.0.1:9840/v1/approval/deepseek-harness`，岛上验签后才弹出审批卡。
- 点「允许」时插件返回 `'allowed-once'`，点「拒绝」时返回 `'rejected'`。这两种情况下 DSH 自己的弹窗都不会出现。
- 点「回 DSH」、岛没开、100 秒没点或者出错时，插件调用 `next()`，交回 DSH 自己的审批界面。它绝不默认同意。
- 每次请求带随机 request id / nonce；响应必须状态码 200、JSON 合法、`decision` 恰好为 `allow` / `deny`、回带同一组 request id / nonce，并通过 Qisland 密钥的 HMAC 校验。任何一项不满足都交回 DSH。
- 这一轮在 DSH 里被取消（`req.signal`）时，插件同步放弃等待。
- 插件不用 prepend 注册，所以排在 `dsh-approval-gate` 之后，被它自动放行的请求不会打扰你。

没有让岛和 DSH 的弹窗同时出来、谁先点算谁，原因是 DSH 目前没有接口能让输掉的那个弹窗自己关掉，见 [discussion #12923](https://github.com/deepseek-ai/deepseek-harness/discussions/12923)。同时弹的话，会留下一张关不掉的旧卡片。

### 安装

桌面版的 `desktop` 配置由应用自己管理，所以 `dsh plugin --profile desktop` 会被拒绝。下面的做法和应用插件页一样，用 DSH 自带的 pnpm 来装：

```powershell
cd $env:USERPROFILE\.dsh\profiles\desktop
$env:ELECTRON_RUN_AS_NODE = '1'
& "D:\Program Files (x86)\DSH\DeepSeek Harness.exe" --expose-internals "D:\Program Files (x86)\DSH\resources\runtime\primary-runtime\dependencies\pnpm\bin\pnpm.mjs" add "dsh-qisland-bridge@file:<Qisland 仓库路径>/integrations/dsh-qisland-bridge"
Remove-Item Env:ELECTRON_RUN_AS_NODE
```

装完以后，在同一目录 `package.json` 的 `dsh.profile.bundles` 里加上 `"dsh-qisland-bridge"`，然后重启 DSH。改之前先备份 `package.json` 和 `cordis.patch.yml`。

旧的 `plugins/dsh-qisland/` 草稿只做旁路通知，已经由这个插件取代。
