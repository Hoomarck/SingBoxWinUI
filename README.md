<div align="center">

<img src="Assets/app-1024.png" width="96" alt="SingBoxWinUI" />

# SingBoxWinUI

**基于 Windows 11 Fluent Design 与云母（Mica）材质的原生轻量级 sing-box 客户端**


<br />

[![Latest Release](https://img.shields.io/github/v/release/Hoomarck/SingBoxWinUI?style=flat-square&label=release&color=238636)](https://github.com/Hoomarck/SingBoxWinUI/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=flat-square&logo=windows&logoColor=white)](#-下载与安装)
[![.NET 8 / WinUI 3](https://img.shields.io/badge/.NET%208-WinUI%203-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#)
[![License](https://img.shields.io/badge/License-MIT-238636?style=flat-square)](LICENSE)

<br />

<a href="https://github.com/Hoomarck/SingBoxWinUI/releases/latest">
  <img src="https://img.shields.io/badge/%F0%9F%93%A5%20%E4%B8%8B%E8%BD%BD%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC-Releases-238636?style=for-the-badge" alt="Download latest release" />
</a>

**[📥 下载最新版本 (Releases)](https://github.com/Hoomarck/SingBoxWinUI/releases/latest)**

</div>

---

## 🖼 软件界面预览

<div align="center">
<img src="Assets/screenshots/overview.png" width="880" alt="概览页" />
<br />
<sub><b>概览</b>　一键启停 · 流量与到期 · 代理模式 · 出口 IP 与连通性检测</sub>
</div>

<table align="center">
  <tr valign="top">
    <td align="center" width="50%">
      <img src="Assets/screenshots/profiles.png" width="420" alt="订阅管理" />
      <br />
      <sub><b>订阅与 Mixin</b>　本地导入 · 远程订阅 · 前后置 Mixin 编辑器</sub>
    </td>
  </tr>
</table>

---

## ✨ 功能亮点

- 🎨 **原生 Fluent 外观**　云母材质 + WinUI 3 原生控件，跟随系统深浅色。
- 💾 **本地与 WebDAV 备份恢复**　配置可打包备份到本地或坚果云 WebDAV，换机器一键还原。

---

## 📦 下载与安装

> 💡 **新手 / 首次安装**
> 下载 **`SingBoxWinUI-Setup-v1.0.0-win-x64-with-core.exe`** —— 内置 `sing-box` 核心与全套规则集，**双击安装即可使用**，安装时可勾选「开机自启」和「桌面快捷方式」。

| 版本 | 适合谁 | 安装包（推荐） | 免安装便携包 |
| :--- | :--- | :--- | :--- |
| **with-core**<br>带核心完整版 | 首次使用 | `SingBoxWinUI-Setup-v1.0.0-win-x64-with-core.exe` | `SingBoxWinUI-v1.0.0-win-x64-with-core.7z` |
| **no-core**<br>不带核心精简版 | 仅更新 |`SingBoxWinUI-Setup-v1.0.0-win-x64-no-core.exe` | `SingBoxWinUI-v1.0.0-win-x64-no-core.7z` |


### 命令行安装（WinGet）

```powershell
winget install doubinanberwan.SingBoxWinUI
```

---

## 🚀 三步快速上手

1. **安装并运行**　双击安装包完成安装，然后打开 **SingBoxWinUI**。
2. **导入订阅**　进入「订阅」页，把订阅链接粘进输入框点「导入远程」，或点「导入本地文件」选择本地的配置 / 订阅文件。
3. **打开开关**　回到「概览」页，打开右上角的开关；需要全局接管就再打开「系统代理」或「TUN 模式」。

> TUN 模式需要管理员权限，开启时程序会自动请求一次授权。

---

## 📄 声明

本项目是 [sing-box](https://github.com/SagerNet/sing-box) 的**第三方图形前端**，与 sing-box / SagerNet 官方无隶属关系。

本仓库**不提供、不内置、不推荐**任何代理节点、订阅服务或配置数据 —— 请使用你自己的订阅。使用者需自行遵守所在国家或地区的法律法规。
