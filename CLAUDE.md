# 零打扰后台验证规范（No Focus-Steal / Zero-Interaction Verification）

**最高优先级铁律 · 适用于本工作区及今后所有任务。**
起因：自动验证时模拟鼠标点击、前置窗口会移动物理光标并抢走用户当前活动窗口焦点，严重打断正常工作。

## 1. 绝对禁止抢鼠标与焦点
任何自动化验证脚本中**严禁使用**以下 API（含等价封装）：
- `SetCursorPos`、`mouse_event`、`SendInput`、`pyautogui`（含 click / moveTo / scroll）
- `SetForegroundWindow`、`BringWindowToTop`、`SwitchToThisWindow`
- `CopyFromScreen` / `ImageGrab.grab` 等整屏抓图
- 依赖"当前活动窗口"的键盘输入（`SendKeys`、模拟快捷键）
- UIA 的 `Control.Click()`（内部会移动物理光标），一律改用控件模式

## 2. 优先静态与无头验证
- 代码 / XAML 改动优先通过 `dotnet build`、XML 解析、`{x:Bind}` 引用校验、直接调用 Service / ViewModel 方法验证。
- 非必要不拉起 GUI；能不启动就不启动。

## 3. 必须截图或交互时的零打扰后台方案
- **不抢焦点启动**：测试窗口出现后立即置底，绝不遮挡用户当前窗口：
  `SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)`
- **后台无感交互**：切页、点击按钮、滚动视图 100% 通过 Windows UI Automation 控件模式直接操作控件：
  `SelectionItemPattern.Select()` / `InvokePattern.Invoke()` / `TogglePattern.Toggle()` /
  `ScrollPattern.SetScrollPercent()` / `ValuePattern.SetValue()`。不动鼠标光标、不发滚轮事件。
- **后台被遮挡截图**：只用 Win32
  `PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT = 0x00000002)`
  从窗口合成表面抓离屏图像；窗口被完全遮挡也能正常截图。禁止任何整屏截屏。
- **收尾**：验证进程立即退出；临时截图写系统临时目录并删除，不在项目根目录留残留文件。

## 4. 大众向极简文档铁律（README.md 专用）

**README 的读者是普通大众用户，不是开发者。**

1. 只回答三个问题：**这是什么 / 去哪下载 / 怎么用**。语言简练直观，用大白话，不写教程以外的东西。
2. **严禁**在 README 堆砌底层实现黑话：`LOH/GC`、`ConcurrentQueue`、`GOMEMLIMIT`、`WindowsPackageType`、WinAppSDK / RID、`RuntimeConfigBuilder`、`.pending` 原子更新、脱敏校验内部细节、`.gitignore` 排除列表、构建脚本参数表等。这类内容只放 `docs/`、`CLAUDE.md` 或项目 Memory（`docs/` 不进版本库）。
3. **禁止覆盖用户精简版 README**：README 由用户在 GitHub 网页端维护。任何会话修改前必须先 `git pull --rebase origin main`，绝不回滚，绝不把旧的长篇技术版贴回去，绝不擅自删改用户已有段落。
4. 后续修改 README 只允许在**保持用户现有精简结构**的前提下，补充必要的「双版本下载说明」与「WinGet 安装命令」；不得自行新增技术章节、展开特性矩阵、追加实现细节或重写措辞。
5. 新增发行版本时，只更新下载表格 / 文件名 / 安装命令，其它段落保持不动。
