## 新插件 / 更新

- [ ] 插件 id：`your-card-id`（和 `plugins/<id>/` 目录名一致）
- [ ] 分级：`declarative`（L2 · 纯 JSON）/ `process`（L3 · 会运行本机程序，需要维护者人工审源码）
- [ ] 访问的域名（≤ 3 个，L2 必填，和 `plugin.json` 的 `permissions.domains` 一致）：
- [ ] 已在本地 Qisland 里「导入 ZIP」或放进插件文件夹试过，卡片能正常显示
- [ ] 已运行 `python tools/qisland_registry.py fix-hashes` 和 `python tools/qisland_registry.py validate`，0 个错误

## 说明

一句话介绍它显示什么、数据从哪来、需不需要 API Key（需要 Key 的接口目前不收）。

> 不用签名：`registry.json.sig` 由维护者合并后统一重签。
