# dsh-qisland-bridge

把 DeepSeek Harness 需要人工确认的审批同步到 [Qisland](https://github.com/Qiuizi/Qisland) 灵动岛。

- DSH 要你确认时，岛上弹审批卡：允许、拒绝或回 DSH；DSH 自己的弹窗只在你选「回 DSH」、岛没开或 100 秒没点时出现。

- 请求带随机 request id / nonce 和 HMAC；只有 Qisland 用同一密钥签回、状态码 200、JSON 合法、`decision` 恰好为 `allow` / `deny`、nonce 与 request id 全部匹配时才采用。
- 绝不默认同意；密钥读不到、超时、断线或任何校验失败都交回 DSH。
- 只连本机 127.0.0.1:9840。Qisland 启动时把密钥写到 `%APPDATA%\Qisland\approval.key`，ACL 只允许当前 Windows 用户访问。

安装（桌面版用 desktop 配置）：

```powershell
dsh plugin --profile desktop add file:<Qisland 仓库路径>/integrations/dsh-qisland-bridge
```

然后在 `~/.dsh/profiles/desktop/package.json` 的 `dsh.profile.bundles` 里确认有 `dsh-qisland-bridge`，重启 DSH。

从旧版桥接升级时，用同一条安装命令重新装入仓库里的版本，再重启 DSH；旧版没有签名校验，不能继续使用。

测试：

```powershell
npm test
```
