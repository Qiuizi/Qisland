# 上岛协议

任何本机程序都可以把一条活动推到 Qisland。服务只听 `127.0.0.1`，默认端口 `9840`。

## Token

把 Token 写进 `%APPDATA%\Qisland\api-token.txt`（一行，可见 ASCII），重启 Qisland 后生效。设了 Token，除健康检查（`GET /health`、`/v1/status`、`/v1/health`）以外的普通请求都要带 `X-Qisland-Token` 或原来的 `X-WinIslands-Token`，否则回 `401`。没有这个文件就不校验。

文件存在但读不了（被占用、没权限、路径是目录）、是空的、或内容不是一段不含空白的可见 ASCII，都算「配置了但无效」：Qisland 启动时记日志并在岛上报错，所有普通读写一律回 `503`（`{"ok":false,"error":"api token invalid","detail":"原因"}`），只有健康检查照常返回；不会退化成不校验。修好文件后重启 Qisland。

仓库里的调用方会自己带上 Token：`scripts/qisland-push.ps1`、WSL 的 `scripts/qisland-push`、`Qisland.exe hook`、`docs/sdk-examples/` 和 DeepSeek Harness 插件都按「环境变量 `QISLAND_TOKEN` → `api-token.txt`」的顺序取 Token。审批接口 `/v1/approval/*` 用自己的 HMAC 签名，不看这个 Token。

## 外部活动的命名空间

本机接口和收件箱写进来的都是「外部活动」，碰不到 Qisland 自己的条目（更新提示、待办提醒、日程、Agent 窗口监视等）：

- 普通卡片的 `id` 会放进 `ext:` 命名空间：推 `"id":"demo-1"`，岛上的 id 是 `ext:demo-1`，响应里的 `id` 也是它。再推同一个 `id` 就是更新；`DELETE /v1/island/push/demo-1` 和 `DELETE /v1/island/push/ext:demo-1` 删的是同一张。
- 接入消息（Agent 会话、JARVIS、信息站、课程快照）的 id 由 Qisland 生成（如 `agent:codex:t1`、`jarvis:task-1`）；和 Qisland 内部条目撞 id 时这条会被忽略。
- `DELETE` 按存储里记录的外部归属撤回：先按原样的 `id` 找（Agent 事件用协议自己的 id），再找 `ext:` 命名空间里的同名卡片；删到了回 `200`（`{"ok":true,"removed":"实际删掉的 id"}`），没有这条或那是内部活动回 `404`（`{"ok":false,"error":"no such external activity"}`），内部条目永远不会被删。
- 卡片里的 `source` 只是显示用的来源标签，不会触发 Qisland 内部的动作（比如写成 `download`、`otp` 不会打开下载文件夹或复制验证码）。认得出的接入消息保留各自的跳转（Codex 会话链接、终端等）。

消息可以带一个顶层 `$type` 说明自己是什么：

| `$type` | 含义 |
|---|---|
| `push` | 已经排好版的卡片（`title` / `body` / `progress` / `duration_seconds`…） |
| `ingest` | 来源自己的事件 JSON（Claude / Codex / 规范化会话 / JARVIS / 课程快照…） |
| `hook` | 原始 hook JSON（`hook_event_name` 等） |

有 `$type` 就照它解析，投到哪个入口都一样。没有 `$type` 的旧消息按 JSON 顶层字段自动判定；收件箱和 HTTP 用的是同一套规则。标题或正文里出现 `kind`、`activities` 不会让一张普通卡片被当成接入消息。

## 直接推一张卡片

`POST /v1/island/push` 和 `POST /v3/island/push` 是 WinIslands 原来的接口，字段没有改。

```json
{
  "id": "demo-1",
  "title": "实验报告",
  "subtitle": "还差一节",
  "body": "今晚 23:00 前交",
  "icon": "\uE7BE",
  "type": "warning",
  "priority": "high",
  "duration_seconds": 30
}
```

`type` 用 `info` / `success` / `warning` / `error`。`priority` 用 `high` / `normal` / `low`。同一个 `id` 再 POST 一次就是更新。默认不延长原来的过期时间。

还支持：

| 方法 | 路径 | 作用 |
|---|---|---|
| DELETE | `/v1/island/push/{id}` | 去掉一条（只能删外部卡片）；删到 `200`，没有这条 `404` |
| GET | `/v1/island/active`（或 `/v3/island/active`） | 只读：当前外部卡片，JSON 数组，每项 `id` / `source` / `type` / `priority` / `progress` / `expires_at`；设了 Token 并带对 Token 时另有 `title` / `body`（没设 Token 时不返回标题和正文） |
| GET | `/v1/health`（或 `/health`、`/v1/status`） | 健康检查，不要 Token，返回 `{"ok":true,"app":"Qisland","product":"Qisland"}` |

上游 WinIslands 的 `PATCH /v3/island/push/{id}` 和 WebSocket `/v3/ws` Qisland 没有实现：更新就用同一个 `id` 再 POST。
字段说明见 `docs/IslandAPI.md`（上游原文，已按 Qisland 的实际接口修订）。这几张路由表和 `docs/sdk-examples/` 由 `ApiContractTests` 对着真实 App 核对。

## 让 Qisland 自己认事件

`POST /v1/qisland/ingest` 吃的是来源自己的 JSON，而不是已经排好版的卡片。认得出的会转成上面的卡片。

### Claude Code hook

钩子从标准输入收到的原文可以直接 POST。`PermissionRequest` 和 `Notification` 会变成「等待确认」，`Stop` 会变成「已完成」。

```json
{
  "hook_event_name": "PermissionRequest",
  "session_id": "s1",
  "cwd": "D:\\src\\app",
  "tool_name": "Bash"
}
```

### Codex notify

Codex 把 JSON 当作参数传给 `notify` 命令。

```json
{ "type": "agent-turn-complete", "thread-id": "t1", "last-assistant-message": "改完了" }
```

`exec-approval-request` 会变成等待确认。

### 规范化会话

Claude、Codex、DeepSeek Harness 插件都可以发这一份：

```json
{
  "source": "deepseek-harness",
  "session_id": "desk",
  "status": "running",
  "title": "DeepSeek Harness 进行中",
  "detail": "正在改 README",
  "quota": { "used": 12, "limit": 100, "unit": "requests" }
}
```

`status`：`running` / `waiting_approval` / `done` / `error`。

`source`：`claude-code` / `codex` / `deepseek-harness`。

### JARVIS（Course Workspace）

```json
{
  "kind": "jarvis",
  "id": "task-1",
  "title": "整理实验报告",
  "body": "第 2 节",
  "status": "running"
}
```

`status` 同上。Qisland 会把 id 收成 `jarvis:task-1`。

### CourseDesk 快照

见 [coursedesk-patch.md](coursedesk-patch.md)。整份快照也可以 POST 到 ingest，根上带 `yuketang` 或 `chaoxing` 就会被认出来。

## curl

```bash
curl -X POST http://127.0.0.1:9840/v1/qisland/ingest \
  -H "Content-Type: application/json" \
  -d '{"source":"claude-code","session_id":"s1","status":"running","title":"Claude Code 进行中","detail":"qisland"}'
```

带 Token：

```bash
curl -X POST http://127.0.0.1:9840/v3/island/push \
  -H "Content-Type: application/json" \
  -H "X-Qisland-Token: 你的token" \
  -d '{"id":"demo","title":"你好","duration_seconds":8}'
```

## PowerShell

```powershell
$body = @{
  source = "codex"
  session_id = "t1"
  status = "waiting_approval"
  title = "Codex 等待确认"
  detail = "git push"
} | ConvertTo-Json
Invoke-RestMethod -Uri "http://127.0.0.1:9840/v1/qisland/ingest" -Method Post `
  -Body $body -ContentType "application/json; charset=utf-8"
```

## Python

```python
import json, urllib.request
req = urllib.request.Request(
    "http://127.0.0.1:9840/v1/qisland/ingest",
    data=json.dumps({
        "kind": "jarvis",
        "id": "task-1",
        "title": "整理实验报告",
        "status": "running",
    }).encode(),
    headers={"Content-Type": "application/json"},
)
urllib.request.urlopen(req).read()
```

## WSL2

`scripts/qisland-push` 从标准输入读 JSON，默认按普通卡片（`push`）处理。发原始 hook 时加 `--type hook`，发接入消息时加 `--type ingest`。脚本总是投 `/v1/qisland/ingest`，由 Qisland 按 `$type` 解析，不再自己判断入口。

镜像网络（`networkingMode=mirrored`）：WSL 里的 `127.0.0.1` 就是 Windows，直接打到 `9840`。

NAT（WSL 默认）：WSL 的 `127.0.0.1` 是 Linux 自己。脚本会再试 `/etc/resolv.conf` 里的 nameserver（一般是 Windows 主机）。Qisland 只绑定 `127.0.0.1`，主机的虚拟网卡地址连不进来，这是故意的，因为接口可以执行推送方带来的命令。NAT 下脚本把 JSON 写到：

`/mnt/c/Users/<Windows 用户名>/AppData/Roaming/Qisland/inbox/*.json`

Qisland 每几秒读一次这个目录，读完就删。所以两种模式都能到岛上，NAT 会慢一两拍。

```bash
# 在 WSL 里
export WIN_USER=你的Windows用户名   # 认不出用户名时再设
echo '{"hook_event_name":"Stop","session_id":"wsl-1"}' | bash /mnt/c/path/to/scripts/qisland-push --type hook
```

Claude 如果装在 WSL 里，把 hook 命令写成这个脚本，而不是 Windows 的 PowerShell。

## 优先级

等待确认高于出错，高于正在进行的签到和随堂题，高于普通高分条目，高于天气和待办。胶囊只放最前面的一条，后面的变成圆点。
