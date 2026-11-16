# dsh-qisland-bridge

把 DeepSeek Harness 需要人工确认的审批同步到 [Qisland](https://github.com/Qiuizi/Qisland) 灵动岛。

- DSH 要你确认时，岛上弹审批卡：允许、拒绝或回 DSH；DSH 自己的弹窗只在你选「回 DSH」、岛没开或 100 秒没点时出现。

- 绝不默认同意；只连本机 127.0.0.1:9840，岛没开时静默跳过。

安装（桌面版用 desktop 配置）：

```powershell
dsh plugin --profile desktop add file:D:/Qiuizi/project/Qisland/integrations/dsh-qisland-bridge
```

然后在 `~/.dsh/profiles/desktop/package.json` 的 `dsh.profile.bundles` 里确认有 `dsh-qisland-bridge`，重启 DSH。
