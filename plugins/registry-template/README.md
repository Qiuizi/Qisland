# qisland-plugins

[Qisland](https://github.com/Qiuizi/Qisland) 插件市场的官方索引仓库：社区投稿的声明式卡片插件、主题包，以及客户端每次刷新都会验签的 `registry.json` 和撤销名单 `revoked.json`。

> 这是仓库模板，放在 Qisland 主仓库的 `plugins/registry-template/` 里。它会原样成为 `Qiuizi/qisland-plugins` 的内容；那个仓库上线前，Qisland 的插件市场照样能用内置的离线目录（同一份 `registry.json`）。

## 目录

```
registry.json          索引：每个插件 / 主题一项（id、版本、分级、分类、权限、下载地址、SHA-256…）
registry.json.sig      维护者对 registry.json 的 Ed25519 签名
revoked.json           撤销名单：按 id（可限定版本）或按包的 SHA-256
revoked.json.sig       撤销名单的签名
plugins/<id>/plugin.json   L2 声明式卡片（纯 JSON，不含代码）
themes/<id>.json       主题包（qisland-theme/1）
tools/qisland_registry.py  校验 / 算哈希 / 签名 / 验签（纯 Python 标准库）
AI_PLUGIN_PROMPT.md    复制给任意 AI，3 分钟生成一个合法的 plugin.json
```

## 客户端怎么用这些文件

- 刷新市场时先取 `https://raw.githubusercontent.com/Qiuizi/qisland-plugins/main/registry.json`，不通再走 `https://cdn.jsdelivr.net/gh/Qiuizi/qisland-plugins@main/registry.json`。
- `registry.json` 必须能用程序里内置的官方公钥验过 `registry.json.sig`，否则整份丢弃，继续用上次验过的缓存或内置目录；`updated_at` 比上次验过的旧也会被丢弃（防止回放旧索引绕过撤销）。
- `revoked.json` 同样验签、同样防回放。命中的已装插件会被立即停用，插件页和岛上会写明原因；正在用的主题包命中时换回默认外观。
- 下载只走 `https://`（默认端口、公网域名），≤ 32 MB，逐字节核对 `sha256`；包里 `plugin.json` 的 id / 分级 / 版本必须和索引一致，申请的域名不能超出索引里写的。
- 用户可以在设置里加第三方源，界面上始终标「第三方源 · 未验签」。第三方源不能冒用官方索引里已有的 id；官方撤销名单对第三方源装的插件同样生效。

## 投稿流程

1. Fork 本仓库，新建 `plugins/<你的id>/plugin.json`（规范见 Qisland 仓库的 [plugins/README.md](https://github.com/Qiuizi/Qisland/blob/main/plugins/README.md)；也可以把 [AI_PLUGIN_PROMPT.md](AI_PLUGIN_PROMPT.md) 复制给 AI 帮你写）。
2. 在 Qisland 里试一遍：把文件夹放进 `%APPDATA%\Qisland\plugins\<id>\`，设置 → 插件 → 重新扫描，打开它，确认卡片和数据都对。
3. 在 `registry.json` 的 `items` 里加一项：

   ```json
   {
     "id": "your-card-id",
     "name": "卡片名",
     "version": "1.0.0",
     "tier": "declarative",
     "category": "dev",
     "author": "你的 GitHub 用户名",
     "description": "一句话：显示什么、数据从哪来",
     "icon": "◆",
     "download_url": "https://raw.githubusercontent.com/Qiuizi/qisland-plugins/main/plugins/your-card-id/plugin.json",
     "sha256": "（运行 fix-hashes 自动填）",
     "permissions": { "domains": ["api.example.com"] },
     "min_app_version": "5.0.0",
     "homepage": "https://github.com/你的用户名/你的仓库"
   }
   ```

   `category` 只能是 `productivity`（效率）/ `dev`（开发）/ `info`（信息）/ `theme`（主题包）。`permissions.domains` 要和 `plugin.json` 里的完全一致。
4. 本地跑：

   ```bash
   python tools/qisland_registry.py fix-hashes   # 按仓库里的文件重写 sha256
   python tools/qisland_registry.py validate     # 0 个错误再提 PR
   ```

5. 提 PR。CI（`.github/workflows/validate-plugin.yml`）会检查 `plugin.json` 结构、L2 安全边界（≤ 3 个公网域名、数据源域名在白名单里、只用 6 种卡片原语、按钮只开 https）和 SHA-256。签名不用你管，合并后维护者统一重签。

### 收什么、不收什么

- **收**：L2 声明式卡片（只读公开接口、免 API Key、不收集任何个人数据）、主题包。
- **暂不收**：需要账号 / API Key / Cookie 的接口；会把用户输入发到第三方的卡片；L3 高级插件（会运行本机程序、没有沙箱）只在维护者逐行审过源码后个别收录，并在索引里标 `"tier": "process"`，客户端安装和启用前都会醒目警告。
- 更新插件：改 `plugin.json` 的 `version`，同步改索引里的 `version`，再跑 `fix-hashes`。新版本多要了域名，用户那边会自动暂停，等他重新确认。

## 维护者：签名与撤销

签名私钥不进任何仓库。签名时临时放进环境变量：

```bash
export QISLAND_REGISTRY_SEED=<32 字节私钥种子的十六进制>
# 先把 registry.json / revoked.json 的 updated_at 改成当前时间（只能往后，不能往前）
python tools/qisland_registry.py sign
python tools/qisland_registry.py validate --strict-sig
unset QISLAND_REGISTRY_SEED
```

撤销一个插件：在 `revoked.json` 的 `items` 里加 `{ "id": "...", "reason": "给用户看的原因" }`（可加 `"versions": ["1.0.0"]` 只撤某几个版本，或用 `"sha256"` 精确撤某个包），改 `updated_at`，重签，推送。客户端下次刷新（启动 2 分钟后、之后每 12 小时，或用户手动刷新）就会停用它。仓库里的两个 `example-*` 条目是格式示例，不对应任何真实插件。

### 哈希与签名规则

- JSON 文件（`registry.json`、`revoked.json`、`plugin.json`、`theme.json`）：先去掉 UTF-8 BOM 和所有 `\r`，再算 SHA-256 / 签名。这样 Windows 上 CRLF 检出和 GitHub raw 返回的 LF 是同一个值。
- zip 包：原始字节。
- `.sig`：一行 Base64，内容是 64 字节 Ed25519（RFC 8032）签名，可带 `ed25519:` 前缀。

客户端（`src/Qisland.Core/PluginMarket.cs`、`Ed25519.cs`）和 `tools/qisland_registry.py` 用的是同一套规则，Qisland 的自检会用内置公钥验这里的两个 `.sig`。

## 许可

插件和主题以各自作者声明的许可发布；未声明的按 MIT。
