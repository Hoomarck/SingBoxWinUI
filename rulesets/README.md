# rulesets/

此目录存放 `type: local` / `format: source` 的规则集 JSON。为遵守隐私与体积约束，**所有 `*.json` 与 `.pending/` 均被 `.gitignore` 排除**，不会随仓库分发。

## 首次运行前准备

默认配置 `Core/config.json` 引用 `../rulesets/geosite_gfw.json`。缺少该文件会导致 `sing-box check` / 内核启动失败。请自行准备：

- 从 [MetaCubeX/meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat) 等公开上游获取 `geosite_*.json` / `geoip_*.json`；
- 或放置最小可用骨架，例如：

```json
{
  "version": 3,
  "rules": [
    { "domain_suffix": ["example.com"] }
  ]
}
```

## 约定

- 一律使用本地 `type: local` 规则集。远程 `remote` 规则集在批量并发下载时容易被 CDN 断连。
- 内核运行期间**不要覆盖**正在使用的规则集文件；客户端会先写入 `rulesets/.pending/`，待安全时点原子替换。
- `.pending/` 为运行期队列目录，同样不进版本库。
