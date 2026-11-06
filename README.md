# SingBoxWinUI

> 基于 **.NET 8 + WinUI 3**（Fluent Design / Mica）的 Windows 原生**绿色便携版** sing-box 客户端。
> A native, portable Windows GUI client for [sing-box](https://github.com/SagerNet/sing-box), built with .NET 8 and WinUI 3.

无需安装、无需 MSIX、不写注册表即可运行；解压后目录可任意移动，配置路径会自动自愈。

> ⚠️ **本仓库只包含客户端界面源码与构建脚本**：不含 `sing-box` 内核二进制、不含任何节点、订阅链接、WebDAV 凭据或规则集数据。
> 内核与规则集请自行准备（见「快速开始」）。`publish.ps1` 生成的发行包已通过脱敏校验，包内 `Core\config.json` 为干净的默认骨架。

---

## 核心特性 / Feature Matrix

| 模块 | 能力 |
| --- | --- |
| **概览页**<br>Home | 一键启停内核；规则 / 全局 / 直连三态切换；系统代理与 TUN 模式；订阅流量与到期时间解析；代理出口 IP 四源回退探测 + Google / GitHub / Cloudflare 三站点并发延迟；实时上下行流量与连接数 |
| **节点策略页**<br>Proxies | 统一延迟双探针测速（Clash API delay + 真实 HTTP 探针）；按拼音 / 延迟排序；分组与节点搜索；**双层节点记忆**（`cache.db` 选中态探测 + `selector.default` 注入 + 启动后 API 对齐） |
| **订阅与规则集**<br>Profiles / RuleSets | 订阅定时更新与本地缓存；前置 / 后置 **Mixin 去节点化占位符合成**（`__ALL__` / `__AUTO__` / `__REGION:hk,jp,sg,us,kr,tw__` / `__FAKEIP__`），换订阅无需改配置；规则集扫描补齐与下载；内核运行中写入 `.pending` 队列实现**原子更新**，空骨架自愈 |
| **活动连接与日志**<br>Connections / Logs | 1 Hz 增量刷新，无闪烁、不重建列表；按进程名 / 域名 / 出口染色；日志采用 `ConcurrentQueue` + 100 ms 批量泵合并写入，高吞吐下 UI 不掉帧 |
| **极致后台轻量模式**<br>Lightweight | 三段式 LOH / GC 压缩；双进程工作集修剪（GUI + 内核）；Go `GOMEMLIMIT` 软上限；托盘常驻、隐藏窗口后内存回落 |
| **设置与维护**<br>Settings | 在线检查并**一键热更新 sing-box 内核**（代理借道下载、预校验、停机换核、`.bak` 回滚）；开机自启计划任务升级；本地 ZIP / WebDAV 云备份与恢复；中英双语**免重启实时切换**；明暗主题、托盘、TUN 提权、单实例 |

<details>
<summary>English summary</summary>

One-click core start/stop, rule/global/direct modes, system proxy & TUN, traffic/expiry parsing, egress-IP and connectivity probes; dual-probe latency testing with pinyin/latency sorting and two-layer selection persistence; scheduled subscription updates, pre/post Mixin with de-noded placeholders, atomic `.pending` rule-set updates; flicker-free 1 Hz connection refresh and 100 ms batched log pump; LOH/GC compaction with working-set trimming and Go `GOMEMLIMIT`; in-app kernel hot-update, autostart, local ZIP / WebDAV backup-restore, live bilingual switching.

</details>

---

## 技术栈

| 项 | 值 |
| --- | --- |
| 框架 | `net8.0-windows10.0.22621.0`，WinUI 3 / Windows App SDK `1.7.260224002` |
| MVVM | CommunityToolkit.Mvvm `8.4.2`（`LangVersion=preview`） |
| 部署 | Unpackaged（`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`），必须显式指定 RID |
| 语言 | C# / XAML；`SatelliteResourceLanguages=zh-Hans;zh-CN;en-US` |
| 内核交互 | 进程双通道日志、Clash API、`sing-box check` 配置预校验 |

---

## 目录结构

```
SingBoxWinUI/
├─ App.xaml(.cs) / MainWindow.xaml(.cs)    # 应用入口与主窗口
├─ Views/ ViewModels/                      # 六个页面及对应 VM
├─ Services/                               # 内核进程、配置合成、订阅、规则集、备份、更新…
├─ Helpers/ Models/                        # 格式化、窗口、对话框、模型
├─ Assets/                                 # 图标
├─ scripts/                                # 词条生成器、内存测量脚本
├─ publish.ps1                             # 绿色版构建 + 脱敏 + 7z + SHA256 + 冒烟测试
├─ Core/                                   # ❗运行时目录（已忽略，需自备 sing-box.exe）
└─ rulesets/                               # ❗规则集 JSON（已忽略，需自备）
```

---

## 快速开始（使用绿色版）

1. 从 GitHub Releases 下载 `SingBoxRunner-win-x64-portable.7z`，用 `.sha256` 校验后解压到任意目录。
2. 自备内核：把 `sing-box.exe` 放到 `SingBoxRunner\Core\`。
3. 自备规则集：把 `geosite_gfw.json` 等 `.json` 放到 `SingBoxRunner\rulesets\`（默认配置引用 `geosite_gfw.json`）。
4. 运行 `SingBoxWinUI.exe`，在「订阅」页添加自己的订阅地址即可。
5. 解压后 `Core\config.json` 是**无任何节点**的默认骨架；`app_settings.json` / `profiles.json` 会在首次启动时自动生成。

> TUN 模式需要管理员权限，程序会在启用时自动请求 UAC 提权。


---

## 声明

本项目是 sing-box 的**第三方图形前端**，与 sing-box / SagerNet 无隶属关系。项目不提供、不内置、不推荐任何代理节点或订阅服务；使用者需自行遵守所在地区的法律法规。
