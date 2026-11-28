# Qisland 插件

Qisland 5.0 的插件分两级，插件页（设置 → 插件）用徽章区分：

| | 安全卡片 · L2 | 高级插件 · L3 |
| --- | --- | --- |
| 是什么 | 只有一个 `plugin.json`，纯声明 | `plugin.json` + 本机脚本 / 程序 |
| 运行代码 | **不运行任何代码**（没有脚本、exe、JS） | 运行 `entry` 指向的 `.ps1 / .cmd / .bat / .exe / .com`，**没有沙箱** |
| 联网 | Qisland 代发 GET：只 `https://`、默认 443 端口、最多 3 个声明过的域名 | 插件自己的事 |
| 启用 | 首次启用确认它要访问的域名 | 一次性安全确认，列出入口文件和 SHA-256 |
| 更新后 | 域名变多 / 新增本机数据源 → 暂停，重新确认 | 入口文件换了或内容变了、或 L2 改成 L3 → 暂停，重新确认 |

两级插件都拿不到微信消息、剪贴板、其他软件的配置和审批接口：Qisland 不给插件任何这类数据，L3 进程的环境变量里也去掉了 Qisland 的令牌。L3 本身没有沙箱，只装信任来源的。

`official/` 里是 6 个官方示例（都是 L2，零隐私、免 API Key），编进了程序，在插件页直接打开就能用：

| id | 内容 | 访问 |
| --- | --- | --- |
| `github-repo-radar` | 公开仓库的 Star / Fork / Issue / 最近推送 | `api.github.com` |
| `countdown-milestone` | 目标日期倒计时 + 进度 | 不联网 |
| `exchange-rate-watch` | 常见外币兑本币参考汇率（每日更新的公开数据） | `open.er-api.com` |
| `hn-top-pulse` | Hacker News 榜首标题、分数、评论数 | `hacker-news.firebaseio.com` |
| `npm-package-stats` | npm 包近 30 天每日下载折线、周下载、周环比 | `api.npmjs.org` |
| `quick-dev-links` | 常用开发入口按钮（最小模板） | 不联网 |

> `dsh-qisland/` 是 DeepSeek Harness 那边的桥接插件，和这里的卡片插件无关。

## L2 声明式卡片：`plugin.json`

```json
{
  "id": "github-repo-radar",
  "name": "GitHub 仓库雷达",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "一句话介绍",
  "icon": "★",
  "accent": "#5AC8FA",
  "tier": "declarative",
  "permissions": { "domains": ["api.github.com"] },
  "config_schema": [
    { "key": "repo", "label": "仓库", "type": "text", "default": "Qiuizi/Qisland", "pattern": "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$" }
  ],
  "sources": {
    "repo": { "type": "https", "url": "https://api.github.com/repos/{{config.repo|path}}", "interval_seconds": 900,
              "headers": { "Accept": "application/vnd.github+json" } }
  },
  "error_text": "GitHub 暂时连不上",
  "card": [
    { "type": "header", "title": "{{config.repo}}", "subtitle": "{{repo.language}}" },
    { "type": "metric", "value": "{{repo.stargazers_count|num}}", "unit": "Star" }
  ]
}
```

### 元数据

| 字段 | 说明 |
| --- | --- |
| `id` | 字母数字开头，只含字母、数字、`.` `-` `_`，1–64 字；也是插件文件夹名 |
| `name` / `version` / `author` / `description` | 展示用，会去掉控制字符并截断 |
| `icon` | 一个 emoji 或字符，画在强调色圆角方块上 |
| `accent` | `#RRGGBB`，图标底色 |
| `tier` | `"declarative"`（也认 `"type": "declarative-card"`） |
| `permissions.domains` | 最多 3 个公网域名，逐个写全；不能写 IP、`localhost`、`*.local` / `.lan` / `.internal` 等内网名，不支持通配符 |
| `permissions.local` | 可选：「本机数据」源，写成 `127.0.0.1:端口` 或内网 `IP:端口`；用户启用时**逐项勾选同意**才会访问，默认不允许 |
| `config_schema` | 最多 8 个参数：`key`（小写字母开头）、`label`、`type`（`text` / `number` / `date` / `select`）、`default`、`options`（select 用）、`pattern`（正则，50 ms 超时）、`placeholder` |
| `error_text` | 拿不到数据时卡片底部的提示 |

L2 清单里出现 `entry`、`script`、`exec`、`command`、`js`、`javascript`、`html` 会直接判为无效。

### 数据源 `sources`（最多 5 个）

| `type` | 字段 | 说明 |
| --- | --- | --- |
| `https` | `url`、`interval_seconds`、`headers` | 网址的 `https://域名` 部分必须写死且在 `domains` 里；只允许 443 端口、不能带用户名密码；路径和查询串里可以用 `{{config.x}}` 和**排在前面**的数据源的值（链式请求，如 HN 先取榜单再取第一条）。`interval_seconds` 最小钳到 60、最大 1 天，默认 300。请求头只放行 `Accept` / `Accept-Language` / `User-Agent`，值不能换行，不能覆盖 `Host` |
| `static` | `data` | 原样给模板用的 JSON，不联网 |
| `countdown` | `target`、`start` | `yyyy-MM-dd` 或 `yyyy-MM-ddTHH:mm`，可用 `{{config.x}}`。提供 `days` `hours` `minutes` `total_hours` `total_days` `elapsed_days` `since_days` `progress`（0–1）`passed` `target` `start` `weekday` |
| `local` | `url` | `http(s)://127.0.0.1:端口/...`，地址必须在 `permissions.local` 里且用户勾选同意过 |

宿主怎么发请求：GET、不带 Cookie 和凭据、不跟随重定向（自己逐跳检查，最多 3 跳，每一跳都必须仍是白名单里的 https 地址）；发之前做一次 DNS 预检、直连时建连那一刻再查一次，域名解析到回环 / 私网 / 链路本地 / CGNAT 等地址一律拒绝；响应体最多 256 KiB，单次 8 秒超时，必须是 JSON。岛看不见、锁屏、刚唤醒没网时不发；数据没变化逐步放慢（最多 4 倍间隔）；失败指数退避（1 分钟起，最长 30 分钟）。卡片上的「刷新」10 秒内只认一次。

### 模板 `{{表达式|过滤器:参数}}`

- 表达式：`config.键`、`now.date` / `now.time` / `now.weekday`、`数据源.路径` 或 `数据源:$.JSON路径`。
- 路径：`.a.b`、`[0]`、`[-1]`（最后一个）、`['带-横线的键']`、`[*]`（展开数组，只能用一次）；最多 24 段，不支持 `..`、过滤表达式和脚本。路径里的 `{键}` 换成配置值（只接受字母数字 `_-`），例如 `rx.rates.{main}`。
- 过滤器（最多 8 个串联）：`num[:小数位]` 千分位、`fixed:n`、`compact`（1.2k / 3.4M）、`percent[:n]`（×100 加 %）、`pct[:n]`、`mul:x` `div:x` `add:x` `sub:x` `round[:n]` `abs` `inv`（倒数）`sign`（正数加 +）、`default:文字`、`trim:n`、`upper` `lower`、`date[:格式]` `ago` `days` `unix`、列表用 `first` `last` `tail:n` `head:n` `sum` `avg` `min` `max` `count` `delta` `growth:n`（最近 n 个之和对比前 n 个的增长率）、网址用 `url` `path`。
- 输出一律是纯文本：去掉控制字符、截断到 160 字；缺数据显示 `—`；值里的 `{{ }}` 不会再被展开。
- 网址模板里的值默认整体 URL 编码（`/ ? # & =` 都会转义）；最后一个过滤器写 `path` 时按段编码、保留 `/`，遇到 `.` / `..` 段就不发请求。

### 6 种卡片原语 `card`（最多 8 个）

| `type` | 字段 |
| --- | --- |
| `header` | `title`（必填）、`subtitle`、`icon`、`status`：`ok` / `warn` / `error` / `idle` / `live`，不写时按数据状态自动 |
| `metric` | `value`（必填）、`unit`、`label`、`delta`（`+`/`-` 开头自动绿 / 红）、`tone` |
| `progress` | `value`（必填，0–1；大于 1 且没写 `max` 时按 0–100）、`max`、`label`、`text`（默认百分比）、`tone`（不写时 ≥70% 黄、≥90% 红） |
| `sparkline` | `points`（必填：数字数组，或 `数据源:$.路径[*].字段`）、`last`（2–60）、`label`、`caption`、`tone` |
| `badge_row` | `badges`：1–6 个 `{ "text": ..., "tone": ... }` |
| `action_row` | `actions`：1–4 个按钮 `{ "label", "icon", "kind" }`；`kind` 只有 `open_url`（`url` 必须 `https://` 且域名写死，用默认浏览器打开）和 `refresh` |

`tone` 可选：`neutral` `green` `amber` `red` `blue` `purple` `teal` `auto`。

## L3 高级插件

沿用旧版 WinIslands 的进程插件格式：`entry`（插件目录里的相对路径）、`arguments`、`config_schema`、`interval_seconds`（最小 30 秒；0 = 只在启用时跑一次）、`timeout_seconds`（1–30，超时结束整个进程树）、`max_output_kb`（≤256）、可选 `sha256`（和入口文件不一致就不加载）。参数通过环境变量 `QISLAND_PLUGIN_CONFIG_<键>` 传入（旧名 `WINISLANDS_PLUGIN_CONFIG_<键>` 也给）。

stdout 输出一段 JSON：

- 新格式 `{"card": [原语...]}`：同上 6 种原语，只能写字面量，按钮同样只允许 https 和刷新；
- 旧格式 `{"components": [{ "text": "...", "progress": 0.4 }]}`：自动转成徽章行和进度条。

每次运行前都会重新算入口文件的 SHA-256，和你确认时不一样就暂停，等你在插件页重新确认。

## 打包与导入

把插件文件夹压成 zip（`plugin.json` 在根目录或唯一的子文件夹里），在插件页「导入 ZIP…」。导入时会拒绝越界路径、超过 200 个文件或解压后超过 64 MB 的包，也不能占用官方插件的 id。导入的插件默认关闭；同 id 的再导入一次就是更新，能力变多会自动暂停等你确认。

插件文件夹在 `%APPDATA%\Qisland\plugins`，开关、参数和授权记录在 `%APPDATA%\Qisland\plugins-state.json`。

## 插件市场

设置 → 插件 → 「插件市场」列出可以一键安装的插件和主题包：

- **内置离线目录**：上面 6 个官方插件和 `themes/` 里的 3 套官方主题，不联网也能装。官方插件「卸载」是隐藏并关闭，随时可以在市场里恢复。
- **官方在线索引**：`https://raw.githubusercontent.com/Qiuizi/qisland-plugins/main/registry.json`（不通时走 jsDelivr 镜像）。索引和撤销名单 `revoked.json` 都必须用程序内置的公钥验过 Ed25519 签名才会被采用；被撤销的已装插件会自动停用，插件页写明原因。
- **第三方源**：在市场页「索引源」里添加，始终标「第三方源 · 未验签」，不能冒用官方 id，安装前会多确认一次。

从市场装的插件放在同一个插件文件夹里，旁边多一个 `.market.json` 记录来源和 SHA-256。索引格式、投稿流程、CI 校验和签名工具见 [registry-template/README.md](registry-template/README.md)；想让 AI 帮你写卡片，把 [registry-template/AI_PLUGIN_PROMPT.md](registry-template/AI_PLUGIN_PROMPT.md) 复制给它。
