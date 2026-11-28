# 让 AI 3 分钟帮你写一个 Qisland 声明式插件

把下面整段（从「你是」开始）复制给任意 AI，把最后一行换成你想要的卡片。它会只回复一个可以直接用的 `plugin.json`。

写好后：放进 `%APPDATA%\Qisland\plugins\<id>\plugin.json`，打开 Qisland 设置 → 插件 → 重新扫描，打开开关就能在岛的组件页看到它。

---

```text
你是 Qisland（Windows 灵动岛）的插件作者。请只输出一个 JSON 代码块：一个合法的 L2 声明式卡片 plugin.json，不要解释。

硬性规则（违反任何一条客户端都会拒绝加载）：
1. 纯 JSON，不能有 entry / script / exec / command / js / javascript / html 字段，不运行任何代码。
2. "tier": "declarative"。id 用小写字母、数字、-，1–64 字，以字母或数字开头。version 用 1.0.0 这种格式。
3. "permissions": { "domains": [...] } 最多 3 个公网域名，逐个写全；不能写 IP、localhost、*.local / .lan / .internal，不支持通配符。不联网就写空数组。
4. sources 最多 5 个，type 只能是：
   - "https"：{ "url": "https://<写死的白名单域名>/路径", "interval_seconds": ≥60, "headers": { "Accept": "application/json" } }。
     只允许 GET 公开 JSON 接口；不要用需要登录、API Key、Cookie 的接口。路径和查询串里可以用 {{config.键}}，
     路径参数写 {{config.键|path}}；可以引用排在前面的数据源的值做链式请求。
   - "static"：{ "data": 任意 JSON }。
   - "countdown"：{ "target": "yyyy-MM-dd", "start": "yyyy-MM-dd" }，提供 days / hours / progress(0–1) / passed 等字段。
5. card 是 1–8 个组件，type 只能是这 6 种：
   - header   { "title"（必填）, "subtitle", "icon", "status": ok|warn|error|idle|live }
   - metric   { "value"（必填）, "unit", "label", "delta", "tone" }
   - progress { "value"（必填，0–1）, "max", "label", "text", "tone" }
   - sparkline{ "points"（必填：数组或 "数据源:$.路径[*].字段"）, "last": 2–60, "label", "caption", "tone" }
   - badge_row{ "badges": 1–6 个 { "text", "tone" } }
   - action_row{ "actions": 1–4 个 { "label", "icon", "kind": "open_url"（url 必须 https:// 且域名写死）或 "refresh" } }
   tone 只能是 neutral / green / amber / red / blue / purple / teal / auto。
6. 文本里用 {{数据源.路径|过滤器}} 取值。路径支持 .a.b、[0]、[-1]、['带-横线']、[*]；不支持 .. 和过滤表达式。
   过滤器：num[:n] fixed:n compact percent[:n] mul:x div:x add:x sub:x round[:n] abs sign default:文字 trim:n upper lower
   date[:格式] ago days first last tail:n head:n sum avg min max count delta growth:n url path，最多串 8 个。
7. config_schema 最多 8 个参数：{ "key": 小写字母开头, "label", "type": text|number|date|select, "default", "options", "pattern", "placeholder" }。
   用户会改的东西（仓库名、包名、日期、币种）都做成参数，给一个能直接工作的默认值。
8. 必填元数据：id、name（≤ 24 字）、version、author、description（一句话：显示什么、数据从哪来）、icon（一个字符或 emoji）、accent（#RRGGBB）。
   再写一个 "error_text"，拿不到数据时显示。
9. 不收集、不发送任何个人数据；拿不到数据时卡片显示 —，不要编造数据。

我想要的卡片：<在这里写，例如「显示某个 GitHub 仓库最新 Release 的版本号、发布时间和下载按钮」>
```

---

小提示：

- 先在浏览器里打开你想用的接口，确认它返回 JSON、不需要登录；把一段返回贴给 AI，它写出来的路径会更准。
- AI 写完后可以再追问一句「逐条对照上面 9 条规则自查一遍」。
- 想投稿到插件市场：见 [README.md](README.md) 的投稿流程。
