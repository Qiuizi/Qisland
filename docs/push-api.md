# 上岛协议

任何本机程序都可以把一条活动推到 Qisland。服务只听 `127.0.0.1`，默认端口 `9840`。设置里如果填了 Token，请求要带 `X-Qisland-Token` 或原来的 `X-WinIslands-Token`。

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
| PATCH | `/v3/island/push/{id}` | 只改写出的字段 |
| DELETE | `/v1/island/push/{id}` | 去掉一条 |
| GET | `/v1/island/active` | 当前卡片 |
| GET | `/v3/ws` | WebSocket |
| GET | `/v1/health` | 健康检查，返回里有 `product: Qisland` |

完整字段仍以仓库里的 `docs/IslandAPI.md` 为准（上游原文）。

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

`scripts/qisland-push` 从标准输入读 JSON。

镜像网络（`networkingMode=mirrored`）：WSL 里的 `127.0.0.1` 就是 Windows，直接打到 `9840`。

NAT（WSL 默认）：WSL 的 `127.0.0.1` 是 Linux 自己。脚本会再试 `/etc/resolv.conf` 里的 nameserver（一般是 Windows 主机）。Qisland 只绑定 `127.0.0.1`，主机的虚拟网卡地址连不进来，这是故意的，因为接口可以执行推送方带来的命令。NAT 下脚本把 JSON 写到：

`/mnt/c/Users/<Windows 用户名>/AppData/Roaming/Qisland/inbox/*.json`

Qisland 每几秒读一次这个目录，读完就删。所以两种模式都能到岛上，NAT 会慢一两拍。

```bash
# 在 WSL 里
export WIN_USER=你的Windows用户名   # 认不出用户名时再设
echo '{"hook_event_name":"Stop","session_id":"wsl-1"}' | bash /mnt/c/path/to/scripts/qisland-push
```

Claude 如果装在 WSL 里，把 hook 命令写成这个脚本，而不是 Windows 的 PowerShell。

## 优先级

等待确认高于出错，高于正在进行的签到和随堂题，高于普通高分条目，高于天气和待办。胶囊只放最前面的一条，后面的变成圆点。
