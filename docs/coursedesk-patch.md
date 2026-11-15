# CourseDesk 补丁

[Qiuizi/CourseDesk](https://github.com/Qiuizi/CourseDesk) 是 Electron 应用。上课、签到、随堂题、作业都在主进程内存里，经 IPC 推给界面。它没有给别的程序留的本地 HTTP，所以 Qisland 不去猜它的内部状态。

干净的接法是：CourseDesk 在已经要弹系统通知、或者快照变了的时候，自己 POST 给 Qisland。这个文件是给 CourseDesk 仓库用的补丁说明，不改那个仓库。

## 1. 通知顺手推一条

`src/main/core/notify.ts` 的 `notify()` 在 `n.show()` 之后加：

```ts
import { pushQisland } from './qisland'

export function notify(options: NotifyOptions): void {
  // ...原来的判断和 new Notification(...) 保持不动
  n.show()
  pushQisland({
    kind: 'coursedesk-event',
    title: options.title,
    body: options.body,
  })
}
```

新建 `src/main/core/qisland.ts`：

```ts
export function pushQisland(body: Record<string, unknown>): void {
  const payload = JSON.stringify(
    body.kind === 'coursedesk-event'
      ? { id: 'course:notify', title: body.title, body: body.body, type: 'warning', priority: 'high', duration_seconds: 20 }
      : body,
  )
  fetch('http://127.0.0.1:9840/v1/qisland/ingest', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: payload,
  }).catch(() => {})
}
```

如果 `kind` 不是快照，ingest 会把它当成一张普通卡片（有 `title` 就收）。

## 2. 把快照写出来或直接推

Qisland 认的是 `AppSnapshot` 里这几块，别的字段可以不给：

```json
{
  "yuketang": {
    "onLesson": [{ "lessonId": "L1", "courseName": "高数" }],
    "lesson": {
      "courseName": "高数",
      "problems": [{ "problemId": "P1", "body": "求极限", "submitState": "pending" }]
    },
    "homework": [{ "id": "H1", "title": "第三章", "courseName": "高数", "leftHours": 5 }]
  },
  "chaoxing": {
    "actives": [{ "id": "A1", "title": "签到", "courseName": "英语", "ongoing": true }],
    "homework": []
  }
}
```

- `onLesson`：正在上课
- `problems` 里还没 `submitted` / `skipped` 的：随堂题
- `actives[].ongoing === true`：学习通签到
- `homework[].leftHours` 在 72 小时内：作业。24 小时内优先级更高

在主进程已经组装好 `AppSnapshot` 的地方（状态广播给渲染进程的旁边）加一行：

```ts
pushQisland(snapshot)
```

或者把同一份 JSON 写到 `%APPDATA%\CourseDesk\qisland-snapshot.json`。Qisland 大约每 30 秒读一次这个文件。设置里的「快照路径」可以改地点。

签到和随堂题的价值最高，所以它们的优先级高于普通作业。

## JARVIS

Course Workspace（JARVIS）还没有公开接口。等它能发 HTTP 的时候，按 [push-api.md](push-api.md) 里的 `kind: jarvis` 推即可，Qisland 这边不用再改。
