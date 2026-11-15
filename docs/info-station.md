# 信息站

信息站是 [Qiuizi/info-station](https://github.com/Qiuizi/info-station)：Tauri 2 + Python 后端 + React。后端只听本机，默认端口 **8765**（`INFO_STATION_PORT`），数据库在 `%LOCALAPPDATA%\InfoStation\info_station.db`。

Qisland 不读这颗库。轮询这几个已经存在的接口更干净，条目形状由信息站自己维护：

| 接口 | 岛上用来做什么 |
|---|---|
| `GET /api/items?view=featured&sort=score&read=unread&limit=5` | 未读高分。默认只留 80 分及以上，可在设置里改 |
| `GET /api/briefs/today?refresh=false` | 今天的简报。`date` 是今天才显示「每日简报已就绪」 |
| `GET /api/deadlines?horizon=14` | 两周内的截止。一天内的优先级更高 |

如果环境变量 `INFO_STATION_TOKEN` 有值，请求要带 `X-Local-Token`。把同一个值填到 Qisland 设置的来源页。

信息站没开时，这几路请求失败会被忽略，岛上留着上一次成功的内容，直到它们过期。

## 可选补丁：通知发出时顺手推一笔

现在的桌面通知走 `notifications` 表，前端再来拉 `/api/notifications/pending`。Qisland 不依赖这个表。如果希望高分通知和岛同时出现，在写入这张表的地方加一次 POST 即可，不必改表结构。

下面这段可以放在信息站后端任意已经决定「这条要通知用户」的位置（例如打完分、`should_notify` 为真的时候）。它失败也不影响信息站自己的通知。

```python
import json
import os
import urllib.request

def push_qisland(title: str, body: str, item_id: int) -> None:
    payload = json.dumps({
        "id": f"info:item:{item_id}",
        "title": title,
        "body": body,
        "type": "info",
        "priority": "normal",
        "duration_seconds": 20,
    }).encode()
    req = urllib.request.Request(
        os.environ.get("QISLAND_URL", "http://127.0.0.1:9840/v1/island/push"),
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    token = os.environ.get("QISLAND_TOKEN", "")
    if token:
        req.add_header("X-Qisland-Token", token)
    try:
        urllib.request.urlopen(req, timeout=1.5).read()
    except Exception:
        pass
```

没有这个补丁，轮询已经够用。补丁只是少等一轮。
