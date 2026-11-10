# 生成 LocalizationService.cs + 批量替换 XAML 文案
import os
import pathlib, re, json, sys

# 本脚本位于 scripts\ 下，而所有相对路径（Services\ / Views\ / MainWindow.xaml）
# 都以项目根为基准；不切换工作目录会把产物写到 scripts\Services\ 里。
os.chdir(pathlib.Path(__file__).resolve().parent.parent)

# (Key, 简体中文, English)
TABLE = [
    # ---------- 导航 / 侧栏 ----------
    ("NavOverview", "概览", "Overview"),
    ("NavProxies", "节点策略", "Proxies"),
    ("NavConnections", "活动连接", "Connections"),
    ("NavLogs", "运行日志", "Logs"),
    ("NavProfiles", "订阅管理", "Profiles"),
    ("NavSettings", "设置", "Settings"),
    ("SidebarMemory", "内存", "Mem"),

    # ---------- 系统托盘 ----------
    ("TrayShowWindow", "显示主界面", "Show Window"),
    ("TrayStartCore", "启动内核", "Start Core"),
    ("TrayStopCore", "停止内核", "Stop Core"),
    ("TrayExit", "彻底退出", "Exit"),
    ("TrayModeRule", "规则 (Rule)", "Rule"),
    ("TrayModeGlobal", "全局 (Global)", "Global"),
    ("TrayModeDirect", "直连 (Direct)", "Direct"),

    # ---------- 通用 ----------
    ("CommonOk", "确定", "OK"),
    ("CommonCancel", "取消", "Cancel"),
    ("CommonSave", "保存", "Save"),
    ("CommonDelete", "删除", "Delete"),
    ("CommonRestoreDefault", "恢复默认", "Restore Default"),
    ("CommonMinutes1", "1 分钟", "1 min"),
    ("CommonMinutes3", "3 分钟", "3 min"),
    ("CommonMinutes5", "5 分钟", "5 min"),
    ("CommonMinutes10", "10 分钟", "10 min"),
    ("CommonMinutes15", "15 分钟", "15 min"),
    ("CommonMinutes30", "30 分钟", "30 min"),
    ("CommonMinutes60", "60 分钟", "60 min"),
    ("CommonHour1", "每 1 小时", "Every hour"),
    ("CommonHour6", "每 6 小时", "Every 6 hours"),
    ("CommonHour12", "每 12 小时", "Every 12 hours"),
    ("CommonHour24", "每 24 小时", "Every 24 hours"),
    ("CommonNever", "不自动更新", "Never"),

    # ---------- 概览 ----------
    ("StatusRunning", "运行中", "Running"),
    ("StatusStopped", "未运行", "Stopped"),
    ("HomeManageProfiles", "切换/管理订阅", "Manage Profiles"),
    ("HomeToggleCore", "启停 sing-box 内核", "Start or stop the sing-box core"),
    ("HomeCurrentProfile", "当前订阅", "Current Profile"),
    ("HomeUpdateProfile", "更新订阅", "Update"),
    ("HomeUpdatedAt", "更新时间", "Updated"),
    ("HomeExpiresAt", "到期时间", "Expires"),
    ("HomeUsedTotal", "已用 / 总流量", "Used / Total"),
    ("HomeUsageA11y", "订阅流量使用比例", "Subscription usage"),
    ("HomeRemoteProfile", "在线订阅", "Remote"),
    ("HomeLocalProfile", "本地配置", "Local"),
    ("HomeTunAdminHint", "TUN 需要管理员权限，否则内核无法创建网卡。",
     "Administrator privileges are required for TUN mode, otherwise the core cannot create the adapter."),
    ("HomeRestartAsAdmin", "以管理员身份重启（TUN 必需）", "Restart as Admin"),
    ("HomeDismiss", "关闭提示", "Dismiss"),
    ("HomeAppIcon", "应用图标", "Application icon"),
    ("HomeProxyMode", "代理模式", "Proxy Mode"),
    ("HomeProxyModeHint", "运行中切换立即生效", "Applies immediately while running"),
    ("HomeModeRule", "规则", "Rule"),
    ("HomeModeGlobal", "全局", "Global"),
    ("HomeModeDirect", "直连", "Direct"),
    ("HomeModeRuleA11y", "规则模式", "Rule mode"),
    ("HomeModeGlobalA11y", "全局模式", "Global mode"),
    ("HomeModeDirectA11y", "直连模式", "Direct mode"),
    ("HomeNetworkSettings", "网络设置", "Network Settings"),
    ("HomeSystemProxy", "系统代理", "System Proxy"),
    ("HomeSystemProxyA11y", "启用系统代理", "Enable system proxy"),
    ("HomeTunMode", "TUN 模式", "TUN Mode"),
    ("HomeTunModeA11y", "启用 TUN 模式", "Enable TUN mode"),

    # ---------- 节点策略 ----------
    ("ProxiesRefresh", "刷新节点", "Refresh"),
    ("ProxiesTestLatency", "一键测速", "Test Latency"),
    ("ProxiesTesting", "测速中", "Testing"),
    ("ProxiesSortDefault", "排序: 默认", "Sort: Default"),
    ("ProxiesSortDelay", "排序: 延迟", "Sort: Delay"),
    ("ProxiesSortName", "排序: 名称", "Sort: Name"),
    ("ProxiesToggleSort", "切换升序 / 降序", "Toggle ascending / descending"),
    ("ProxiesToggleSortA11y", "切换升降序", "Toggle sort order"),
    ("ProxiesClickCardHint", "点击卡片切换节点", "Click a card to switch node"),
    ("ProxiesStartCoreFirst", "请先在「概览与日志」中启动内核", "Start the core on the Overview page first"),

    # ---------- 订阅管理 ----------
    ("ProfilesNewName", "订阅名称", "Profile name"),
    ("ProfilesNewNameA11y", "新订阅名称", "New profile name"),
    ("ProfilesNewUrl", "订阅链接（https://...）", "Subscription URL (https://...)"),
    ("ProfilesNewUrlA11y", "新订阅链接", "New subscription URL"),
    ("ProfilesImportRemote", "导入在线订阅", "Import Remote"),
    ("ProfilesImportLocal", "导入本地文件", "Import Local File"),
    ("ProfilesUpdateAll", "更新全部订阅", "Update All"),
    ("ProfilesUpdateNow", "立即更新", "Update"),
    ("ProfilesEnable", "启用此订阅", "Enable this profile"),
    ("ProfilesInUse", "当前使用", "In use"),
    ("ProfilesRenameEdit", "重命名/编辑", "Rename / Edit"),
    ("ProfilesInterval", "定时更新", "Auto update"),
    ("ProfilesIntervalA11y", "定时更新周期", "Update interval"),
    ("ProfilesMixinTitle", "全局拓展配置（Mixin）", "Global Mixin"),
    ("ProfilesPreMixin", "全局前置拓展", "Pre-mixin"),
    ("ProfilesPostMixin", "全局后置拓展", "Post-mixin"),
    ("ProfilesPreMixinHint", "覆盖订阅默认字段，并将规则/节点前置插入（高优先级抢占）",
     "Overrides subscription fields and prepends rules / nodes (higher priority)."),
    ("ProfilesPostMixinHint", "在订阅配置末尾追加自定义规则、规则集或自建节点",
     "Appends custom rules, rule-sets or nodes to the end of the subscription config."),
    ("ProfilesEditPreJson", "编辑前置 JSON", "Edit pre-mixin JSON"),
    ("ProfilesEditPostJson", "编辑后置 JSON", "Edit post-mixin JSON"),
    ("ProfilesEnablePreA11y", "启用全局前置拓展", "Enable pre-mixin"),
    ("ProfilesEnablePostA11y", "启用全局后置拓展", "Enable post-mixin"),
    ("ProfilesEditTitle", "编辑订阅", "Edit Profile"),
    ("ProfilesDeleteTitle", "删除订阅", "Delete Profile"),
    ("ProfilesPreMixinJson", "前置拓展 JSON", "Pre-mixin JSON"),
    ("ProfilesPostMixinJson", "后置拓展 JSON", "Post-mixin JSON"),
    ("ProfilesOpenWithEditor", "用系统默认编辑器打开文件", "Open with the system default editor"),
    ("ProfilesEditPreMixinTitle", "编辑全局前置拓展", "Edit Global Pre-mixin"),
    ("ProfilesEditPostMixinTitle", "编辑全局后置拓展", "Edit Global Post-mixin"),
    ("ProfilesLocalUrlReadonly", "本地配置不可修改链接", "A local profile's URL cannot be changed"),

    # ---------- 活动连接 ----------
    ("ConnPause", "暂停刷新", "Pause"),
    ("ConnResume", "继续刷新", "Resume"),
    ("ConnShowColumns", "显示参数", "Columns"),
    ("ConnCloseAll", "全部断开", "Close All"),
    ("ConnSortByA11y", "排序维度", "Sort by"),
    ("ConnSearchA11y", "连接搜索", "Connection search"),
    ("ConnSearchPlaceholder", "搜索 域名 / IP / 进程 / 规则 / 出站",
     "Search host / IP / process / rule / outbound"),
    ("ConnColHost", "目标域名", "Host"),
    ("ConnColDestinationIp", "目标 IP 地址", "Destination IP"),
    ("ConnColProcess", "进程名称", "Process"),
    ("ConnColRule", "命中路由规则", "Rule"),
    ("ConnColChain", "出站节点链路", "Outbound chain"),
    ("ConnOutboundFilterA11y", "出站筛选", "Outbound filter"),
    ("ConnColInbound", "入站与协议", "Inbound"),
    ("ConnColOutboundGroup", "出站策略组", "Outbound group"),
    ("ConnColStart", "建立时间", "Started"),
    ("ConnColDuration", "连接持续时长", "Duration"),
    ("ConnColUploadRate", "上传速度", "Upload"),
    ("ConnColDownloadRate", "下载速度", "Download"),
    ("ConnColUploadTotal", "上传总流量", "Uploaded"),
    ("ConnColDownloadTotal", "下载总流量", "Downloaded"),
    ("ConnColTrafficTotal", "累计上下行流量", "Total traffic"),
    ("ConnColSpeedTotal", "实时上下行速度", "Current speed"),
    ("ConnCloseA11y", "关闭连接", "Close connection"),
    ("ConnEmptyFiltered", "没有匹配的连接", "No matching connections"),
    ("ConnEmpty", "暂无活动连接", "No active connections"),

    # ---------- 运行日志 ----------
    ("LogsSearchPlaceholder", "搜索日志内容、规则或节点...", "Search log text, rules or nodes..."),
    ("LogsSearchA11y", "日志搜索", "Log search"),
    ("LogsLevelA11y", "日志级别筛选", "Log level filter"),
    ("LogsLevelAll", "全部级别", "All levels"),
    ("LogsLevelInfoUp", "仅 Info 及以上", "Info and above"),
    ("LogsLevelWarnUp", "仅 Warning 及以上", "Warning and above"),
    ("LogsLevelError", "仅 Error", "Error only"),
    ("LogsAutoScroll", "自动滚动", "Auto scroll"),
    ("LogsCopyAllA11y", "复制全部日志", "Copy all logs"),
    ("LogsCopy", "复制", "Copy"),
    ("LogsExportA11y", "导出日志", "Export logs"),
    ("LogsExport", "导出文件", "Export"),
    ("LogsClearA11y", "清空日志", "Clear logs"),
    ("LogsClear", "清空", "Clear"),
    ("LogsEmpty", "暂无日志", "No logs yet"),

    # ---------- 设置：分区标题 ----------
    ("SetSectionStartup", "启动与后台", "Startup & Background"),
    ("SetSectionGeneral", "通用与网络", "General & Network"),
    ("SetSectionTun", "TUN 虚拟网卡设置", "TUN Settings"),
    ("SetSectionBackup", "备份与恢复", "Backup & Restore"),
    ("SetSectionAdvanced", "高级与目录", "Advanced"),

    # ---------- 设置：语言 ----------
    ("SetLanguageLabel", "语言 / Language", "Language"),
    ("SetLanguageHint", "切换后立即生效，无需重启", "Applies immediately, no restart needed"),
    ("SetLanguageChinese", "简体中文", "简体中文"),
    ("SetLanguageEnglish", "English", "English"),

    # ---------- 设置：外观 ----------
    ("SetAppearance", "外观主题", "Theme"),
    ("SetAppearanceA11y", "外观主题", "Theme"),
    ("SetThemeSystem", "跟随系统", "System"),
    ("SetThemeLight", "浅色", "Light"),
    ("SetThemeDark", "深色", "Dark"),

    # ---------- 设置：启动与后台 ----------
    ("SetAutoStartApp", "开机自动启动", "Launch at sign-in"),
    ("SetAutoStartAppHint", "登录 Windows 后自动启动客户端", "Start the app automatically after you sign in to Windows."),
    ("SetAutoStartAppA11y", "开机自动启动", "Launch at sign-in"),
    ("SetSilentTray", "自启时静默驻留托盘", "Start silently in tray"),
    ("SetSilentTrayHint", "开机启动时不显示主窗口，直接缩到托盘", "Do not show the main window at startup; go straight to the tray."),
    ("SetSilentTrayA11y", "自启时静默驻留托盘", "Start silently in tray"),
    ("SetAutoStartCore", "打开软件时自动启动内核", "Start core on launch"),
    ("SetAutoStartCoreHint", "客户端启动后自动拉起 sing-box", "Bring up sing-box automatically when the app starts."),
    ("SetAutoStartCoreA11y", "打开软件时自动启动内核", "Start core on launch"),
    ("SetCloseToTray", "关闭主窗口时隐藏到系统托盘", "Close to tray"),
    ("SetCloseToTrayHint", "关闭时保持后台运行；从托盘菜单「彻底退出」才会真正结束",
     "Keeps running in the background; use \"Exit\" in the tray menu to really quit."),
    ("SetCloseToTrayA11y", "关闭主窗口时隐藏到系统托盘", "Close to tray"),
    ("SetLightweight", "后台轻量模式", "Background lightweight mode"),
    ("SetLightweightHint", "隐藏到系统托盘达到指定时间后，自动暂停 UI 遥测轮询、释放非活动页面缓存并压缩进程内存工作集",
     "After the window stays hidden in the tray for the given time, UI telemetry polling pauses, inactive page caches are released and the working set is trimmed."),
    ("SetLightweightA11y", "后台轻量模式", "Background lightweight mode"),
    ("SetLightweightDelayA11y", "轻量模式触发时间", "Lightweight mode delay"),
    ("SetAutoStartAppAdminHint", "当前非管理员，仅创建普通权限自启；若需开机自动开启 TUN，请先提权后再开启此开关。",
     "Running without administrator rights, so auto-start uses normal privileges. To start TUN at boot, elevate first and then enable this switch."),

    # ---------- 设置：通用与网络 ----------
    ("SetIpv6", "启用 IPv6 支持", "Enable IPv6"),
    ("SetIpv6Hint", "控制 DNS 解析策略与 TUN 虚拟网卡的 IPv6 双栈支持（修改后若内核正在运行将自动重启生效）",
     "Controls the DNS strategy and IPv6 dual-stack support for the TUN adapter (the core restarts automatically if it is running)."),
    ("SetIpv6A11y", "启用 IPv6 支持", "Enable IPv6"),
    ("SetUnifiedDelay", "统一延迟测试", "Unified latency test"),
    ("SetUnifiedDelayHint", "开启后将连续进行两次延迟测试并取第二次结果，消除不同协议首次连接握手带来的延迟偏差",
     "Runs two latency probes and keeps the second result, removing first-connection handshake bias between protocols."),
    ("SetUnifiedDelayA11y", "统一延迟测试", "Unified latency test"),
    ("SetSpeedTestUrl", "节点测速地址", "Latency test URL"),
    ("SetSpeedTestUrlHint", "用于策略组「一键测速」的探测地址，需返回 2xx",
     "Probe URL used by \"Test Latency\"; it must return 2xx."),
    ("SetSpeedTestUrlA11y", "节点测速地址", "Latency test URL"),
    ("SetAutoTestDelay", "后台自动检测延迟", "Auto latency test"),
    ("SetAutoTestDelayHint", "内核运行期间按固定周期静默重测策略组延迟，并按当前排序规则刷新顺序",
     "Silently re-tests group latency on a fixed interval while the core runs and re-applies the current sort order."),
    ("SetAutoTestDelayA11y", "后台自动检测延迟", "Auto latency test"),
    ("SetAutoTestIntervalA11y", "自动测速间隔", "Auto test interval"),

    # ---------- 设置：TUN ----------
    ("SetTunMtuHint", "默认 9000，链路异常时可尝试降到 1500",
     "Defaults to 9000; lower it to 1500 if the link misbehaves."),
    ("SetTunInterface", "网卡名称", "Interface name"),
    ("SetTunInterfaceHint", "写入 interface_name，留空则由内核自动命名",
     "Written to interface_name; leave empty to let the core pick a name."),
    ("SetTunInterfaceA11y", "TUN 网卡名称", "TUN interface name"),
    ("SetTunStack", "网络栈", "Network stack"),
    ("SetTunStackHint", "system 走系统协议栈，gvisor 兼容性更好，mixed 由内核自动选择",
     "system uses the OS stack, gvisor is more compatible, mixed lets the core decide."),
    ("SetTunStackA11y", "TUN 网络栈", "TUN network stack"),
    ("SetTunAutoRoute", "自动配置路由", "Auto route"),
    ("SetTunAutoRouteHint", "auto_route，接管系统路由表", "auto_route: takes over the system routing table."),
    ("SetTunAutoRouteA11y", "TUN 自动配置路由", "TUN auto route"),
    ("SetTunStrictRoute", "严格路由", "Strict route"),
    ("SetTunStrictRouteHint", "strict_route，防止流量绕过 TUN 造成泄漏",
     "strict_route: prevents traffic from bypassing the TUN adapter."),
    ("SetTunStrictRouteA11y", "TUN 严格路由", "TUN strict route"),
    ("SetTunApply", "应用并重启 TUN", "Apply and restart TUN"),
    ("SetTunApplyHint", "内核正以 TUN 运行时，点右侧按钮会按新参数热重启。",
     "When the core is running with TUN, the button on the right hot-restarts it with the new settings."),
    ("SetAppliesImmediately", "修改后立即生效", "Applies immediately"),

    # ---------- 设置：高级与目录 ----------
    ("SetCoreVersion", "sing-box 内核版本", "sing-box version"),
    ("SetCoreDirectory", "配置与内核目录", "Config & core directory"),
    ("SetCoreDirectoryHint", "config.json、sing-box.exe 与运行配置都在这里",
     "config.json, sing-box.exe and the runtime config live here."),
    ("SetOpenDirectory", "打开目录", "Open folder"),

    # ---------- 设置：规则集 ----------
    ("SetRuleSets", "规则集（Rule-sets）管理与更新", "Rule-sets"),
    ("SetRuleSetsAutoUpdate", "自动更新", "Auto update"),
    ("SetRuleSetsAutoUpdateA11y", "自动更新规则集", "Auto update rule-sets"),
    ("SetRuleSetsUpdateAll", "立即更新全部", "Update All"),
    ("SetRuleSetsUpdateAllA11y", "立即更新全部规则集", "Update all rule-sets"),
    ("SetRuleSetsUrl", "规则集下载地址", "Rule-set base URL"),
    ("SetRuleSetsUrlPlaceholder", "规则集下载地址模板，支持 {category} {short} {name} {file}",
     "Rule-set URL template; supports {category} {short} {name} {file}"),
    ("SetRuleSetsUrlReset", "恢复默认规则集地址", "Restore default rule-set URL"),
    ("SetRuleSetsList", "查看规则集文件列表", "Show rule-set files"),

    # ---------- 设置：备份 ----------
    ("SetBackupHint", "备份包含客户端设置、全部订阅配置与拓展配置（不含内核程序与缓存）",
     "Backups contain app settings, all profiles and mixin configs (no core binary or cache)."),
    ("SetExportBackup", "导出本地备份", "Export Backup"),
    ("SetExportBackupA11y", "导出本地备份", "Export local backup"),
    ("SetRestoreLocal", "从本地备份恢复", "Restore Local"),
    ("SetRestoreLocalA11y", "从本地备份恢复", "Restore from local backup"),
    ("SetAutoWebDav", "启动时自动 WebDAV 备份", "Auto WebDAV backup on start"),
    ("SetAutoWebDavHint", "每次启动后在后台静默上传一次，失败只记日志不打扰",
     "Uploads once in the background after each start; failures are only logged."),
    ("SetAutoWebDavA11y", "启动时自动 WebDAV 备份", "Auto WebDAV backup on start"),
    ("SetWebDavUrl", "WebDAV 地址", "WebDAV URL"),
    ("SetWebDavUrlPlaceholder", "WebDAV 地址（https://dav.jianguoyun.com/dav/SingBoxBackup/）",
     "WebDAV URL (https://dav.jianguoyun.com/dav/SingBoxBackup/)"),
    ("SetWebDavUser", "WebDAV 用户名", "WebDAV username"),
    ("SetWebDavPassword", "WebDAV 密码", "WebDAV password"),
    ("SetTestConnection", "测试连接", "Test"),
    ("SetTestConnectionA11y", "测试 WebDAV 连接", "Test WebDAV connection"),
    ("SetWebDavBackupNow", "立即 WebDAV 备份", "Backup Now"),
    ("SetWebDavBackupNowA11y", "立即 WebDAV 备份", "Back up to WebDAV now"),
    ("SetRestoreWebDav", "从 WebDAV 恢复", "Restore from WebDAV"),
    ("SetRestoreWebDavA11y", "从 WebDAV 恢复", "Restore from WebDAV"),
    ("SetRestoreConfirm", "恢复备份将覆盖当前的客户端设置、所有订阅及拓展配置，是否继续？",
     "Restoring a backup overwrites the current app settings, all profiles and mixin configs. Continue?"),
    ("SetRestoreConfirmButton", "确认恢复", "Restore"),
    ("SetBackupFileType", "备份包", "Backup archive"),

    # ---------- 连接性检测 / 内核更新（新增功能） ----------
    ("ConnCardTitle", "网络连通性与出口信息", "Connectivity & Exit IP"),
    ("ConnExitIp", "出口 IP", "Exit IP"),
    ("ConnRegionIsp", "地区 / 运营商", "Region / ISP"),
    ("ConnChecking", "检测中…", "Checking…"),
    ("ConnRecheck", "立即检测", "Check now"),
    ("ConnShowIp", "显示 IP", "Show IP"),
    ("ConnHideIp", "隐藏 IP", "Hide IP"),
    ("ConnCopyIp", "复制 IP", "Copy IP"),
    ("ConnNotConnected", "未连接", "Not connected"),
    ("ConnCoreStopped", "内核未启动", "Core is not running"),
    ("ConnFailed", "检测失败", "Check failed"),
    ("ConnTimeout", "超时", "Timeout"),
    ("ConnViaProxy", "经本地代理通道", "Via local proxy"),
    ("ConnViaDirect", "直连探测", "Direct connection"),
    ("ConnCopied", "已复制出口 IP", "Exit IP copied"),
    ("ConnUnknownRegion", "未知地区", "Unknown region"),
    ("ConnNoIp", "--", "--"),
    ("ConnIpMaskA11y", "切换 IP 显示", "Toggle IP visibility"),
    ("ConnLatencyA11y", "连通性延迟", "Connectivity latency"),
    ("ConnGoogle", "Google", "Google"),
    ("ConnGithub", "GitHub", "GitHub"),
    ("ConnCloudflare", "Cloudflare", "Cloudflare"),
    ("SingBoxVersion", "sing-box 版本", "sing-box version"),
    ("KernelCardTitle", "sing-box 内核版本", "sing-box Kernel"),
    ("KernelCurrentVersion", "当前版本", "Current version"),
    ("KernelCheckUpdate", "检查更新", "Check for updates"),
    ("KernelUpdateNow", "立即更新", "Update now"),
    ("KernelChangelog", "查看更新日志", "View changelog"),
    ("KernelUpToDate", "已是最新版本", "Up to date"),
    ("KernelNotInstalled", "未安装", "Not installed"),
    ("KernelChecking", "正在检查更新…", "Checking for updates…"),
    ("KernelCheckFailed", "检查更新失败", "Update check failed"),
    ("KernelNotChecked", "尚未检查更新", "Not checked yet"),
    ("KernelDownloading", "正在下载内核…", "Downloading core…"),
    ("KernelInstalling", "正在替换内核…", "Replacing core…"),
    ("KernelRestarting", "正在重启内核…", "Restarting core…"),
    ("KernelUpdated", "内核已更新", "Core updated"),
    ("KernelUpdateFailed", "更新失败", "Update failed"),
    ("KernelRolledBack", "已回滚到旧内核", "Rolled back to previous core"),
    ("KernelNoAsset", "未找到当前架构的发行包", "No release asset for this architecture"),
    ("KernelLightweightBlocked", "后台轻量模式下不自动检查更新", "No automatic update check in lightweight mode"),
    ("KernelUpdateHint", "更新会短暂停机换核，内核运行中会自动恢复连接", "Updating briefly stops the core; connections resume automatically"),
    ("KernelUpdateConfirmTitle", "更新内核", "Update core"),
    ("KernelUpdateConfirm", "将下载并替换 Core\\sing-box.exe，是否继续？", "Download and replace Core\\sing-box.exe. Continue?"),
    ("KernelAssetSize", "下载大小", "Download size"),
    ("KernelValidateFailed", "新内核预校验失败，已中止更新", "New core failed pre-validation; update aborted"),
    ("PickerAllFiles", "所有文件 (*.*)", "All files (*.*)"),
    ("PickerOpenTitle", "选择文件", "Select a file"),
    ("PickerSaveTitle", "保存文件", "Save file"),
    ("PickerFailedTitle", "文件对话框打开失败", "File dialog failed"),
    ("ProfilesImportFailedTitle", "导入失败", "Import failed"),
    ("ProfilesMixinReload", "重新读取文件", "Reload from disk"),
    ("AboutCardTitle", "关于", "About"),
    ("AboutGitHub", "GitHub 主页", "GitHub"),
    ("AboutCheckUpdate", "检查更新", "Check for updates"),
    ("AboutChecking", "正在检查更新…", "Checking for updates…"),
    ("AboutDownload", "前往下载", "Download"),
    ("AboutAutoCheck", "自动检查更新", "Automatically check for updates"),
    ("AboutAutoCheckHint", "启动应用时自动检测客户端新版本", "Automatically check for new client releases on startup"),
    ("AboutNotChecked", "尚未检查更新", "Not checked yet"),
    ("AboutUpToDate", "已是最新版本", "Up to date"),
    ("AboutCheckFailed", "检查更新失败", "Update check failed"),
    ("AboutLightweightBlocked", "后台轻量模式下不自动检查更新", "No automatic update check in lightweight mode"),
    ("StatusRunningProxy", "运行中 (系统代理)", "Running (System Proxy)"),
    ("StatusRunningTun", "运行中 (TUN)", "Running (TUN)"),
    ("StatusRunningBoth", "运行中 (系统代理 + TUN)", "Running (System Proxy + TUN)"),
    ("HomeSwitchHint", "开启任一开关即启动内核，全部关闭即停止内核", "Turn on either switch to start the core; turn both off to stop it"),
]

_seen_zh = set()
_deduped = []
for _k, _z, _e in TABLE:
    if _z in _seen_zh:
        continue
    _seen_zh.add(_z)
    _deduped.append((_k, _z, _e))
TABLE = _deduped

KEYS = [k for k, _, _ in TABLE]
ZH = {k: z for k, z, _ in TABLE}
EN = {k: e for k, _, e in TABLE}

# 反向索引：中文 -> key（供 XAML 批量替换）
ZH2KEY = {}
for k, z, _ in TABLE:
    if z in ZH2KEY and ZH2KEY[z] != k:
        print(f"[warn] 中文重复: {z} -> {ZH2KEY[z]} / {k}", file=sys.stderr)
    ZH2KEY[z] = k

# ============================================================ 1) 生成 C#
lines = []
lines.append("using CommunityToolkit.Mvvm.ComponentModel;")
lines.append("")
lines.append("namespace SingBoxWinUI.Services;")
lines.append("")
lines.append("/// <summary>")
lines.append("/// 响应式本地化服务。")
lines.append("/// 不走 ApplicationLanguages / .resw：Unpackaged 模式下 PrimaryLanguageOverride 无效，")
lines.append("/// 而且 resw 切换必须重启。这里把所有词条做成强类型属性，切语言时逐个 PropertyChanged，")
lines.append("/// 界面上所有 Mode=OneWay 的 x:Bind 会当场重算，无需重启。")
lines.append("/// </summary>")
lines.append("public sealed class LocalizationService : ObservableObject")
lines.append("{")
lines.append("    public const string ChineseLanguage = \"zh-CN\";")
lines.append("    public const string EnglishLanguage = \"en-US\";")
lines.append("")
lines.append("    public static LocalizationService Instance { get; } = new();")
lines.append("")
lines.append("    private static readonly Dictionary<string, (string Zh, string En)> Strings = new(StringComparer.Ordinal)")
lines.append("    {")
for k, z, e in TABLE:
    zz = z.replace('\\', '\\\\').replace('"', '\\"')
    ee = e.replace('\\', '\\\\').replace('"', '\\"')
    lines.append(f'        ["{k}"] = ("{zz}", "{ee}"),')
lines.append("    };")
lines.append("")
lines.append("    /// <summary>所有词条名，切语言时逐个通知。</summary>")
lines.append("    private static readonly string[] AllKeys =")
lines.append("    [")
for i in range(0, len(KEYS), 6):
    chunk = ", ".join(f'"{k}"' for k in KEYS[i:i + 6])
    lines.append(f"        {chunk},")
lines.append("    ];")
lines.append("")
lines.append("    private string _currentLanguage = ChineseLanguage;")
lines.append("")
lines.append("    private LocalizationService()")
lines.append("    {")
lines.append("    }")
lines.append("")
lines.append("    /// <summary>当前语言：zh-CN 或 en-US。</summary>")
lines.append("    public string CurrentLanguage => _currentLanguage;")
lines.append("")
lines.append("    public bool IsEnglish => string.Equals(_currentLanguage, EnglishLanguage, StringComparison.OrdinalIgnoreCase);")
lines.append("")
lines.append("    /// <summary>语言变化后触发，供 ViewModel 的拼接文案与托盘菜单刷新。</summary>")
lines.append("    public event EventHandler? LanguageChanged;")
lines.append("")
lines.append("    /// <summary>切换语言；语言没变时直接返回，避免无谓的全量刷新。</summary>")
lines.append("    public void SetLanguage(string? language)")
lines.append("    {")
lines.append("        var normalized = string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase)")
lines.append("            ? EnglishLanguage")
lines.append("            : ChineseLanguage;")
lines.append("")
lines.append("        if (string.Equals(normalized, _currentLanguage, StringComparison.Ordinal))")
lines.append("        {")
lines.append("            return;")
lines.append("        }")
lines.append("")
lines.append("        _currentLanguage = normalized;")
lines.append("")
lines.append("        // 逐条通知：x:Bind 生成的是按属性名分派的 switch，空字符串不一定能命中，")
lines.append("        // 所以这里既发空串（覆盖支持\"全部刷新\"的实现）也逐条发一遍。")
lines.append("        OnPropertyChanged(string.Empty);")
lines.append("        foreach (var key in AllKeys)")
lines.append("        {")
lines.append("            OnPropertyChanged(key);")
lines.append("        }")
lines.append("")
lines.append("        OnPropertyChanged(nameof(CurrentLanguage));")
lines.append("        OnPropertyChanged(nameof(IsEnglish));")
lines.append("")
lines.append("        LanguageChanged?.Invoke(this, EventArgs.Empty);")
lines.append("    }")
lines.append("")
lines.append("    /// <summary>按词条名取当前语言文案，找不到时回退成 key 本身。</summary>")
lines.append("    public string T(string key)")
lines.append("    {")
lines.append("        if (!Strings.TryGetValue(key, out var pair))")
lines.append("        {")
lines.append("            return key;")
lines.append("        }")
lines.append("")
lines.append("        return IsEnglish ? pair.En : pair.Zh;")
lines.append("    }")
lines.append("")
lines.append("    public string this[string key] => T(key);")
lines.append("")
lines.append("    /// <summary>")
lines.append("    /// 临时状态文案的双语取值。这类字符串只显示一次、不需要跟随语言实时刷新，")
lines.append("    /// 与其为每句话都在词条表里占一行，不如就地给两种语言。")
lines.append("    /// </summary>")
lines.append("    public string Say(string zh, string en) => IsEnglish ? en : zh;")
lines.append("")
lines.append("    // ==================== 带参数的拼接文案 ====================")
lines.append("")
lines.append("    public string LogCount(int count) => IsEnglish ? $\"{count} entries\" : $\"共 {count} 条\";")
lines.append("")
lines.append("    public string LogsCopied(int count) => IsEnglish ? $\"Copied {count} entries\" : $\"已复制 {count} 条日志\";")
lines.append("")
lines.append("    public string LogsCopyEmpty() => IsEnglish ? \"No logs to copy\" : \"没有可复制的日志\";")
lines.append("")
lines.append("    public string LogsCleared() => IsEnglish ? \"Logs cleared\" : \"已清空日志\";")
lines.append("")
lines.append("    public string ConnectionsCount(int count) => IsEnglish ? $\"Connections ({count})\" : $\"活动连接 ({count})\";")
lines.append("")
lines.append("    public string AllOutbounds() => IsEnglish ? \"All outbounds\" : \"全部出站\";")
lines.append("")
lines.append("    public string RemainingDays(int days) => IsEnglish ? $\"{days} days left\" : $\"剩余 {days} 天\";")
lines.append("")
lines.append("    public string ExpiredOn(string date) => IsEnglish ? $\"{date} (expired)\" : $\"{date}（已过期）\";")
lines.append("")
lines.append("    public string ExpiresOn(string date, int days) => IsEnglish")
lines.append("        ? $\"{date} ({days} days left)\"")
lines.append("        : $\"{date}（剩余 {days} 天）\";")
lines.append("")
lines.append("    public string ExpireUnknown() => IsEnglish ? \"Lifetime / Unknown\" : \"长期有效 / 未知\";")
lines.append("")
lines.append("    public string UsedAmount(string amount) => IsEnglish ? $\"Used {amount}\" : $\"已用 {amount}\";")
lines.append("")
lines.append("    public string RemainingAmount(string amount) => IsEnglish ? $\"{amount} left\" : $\"剩余 {amount}\";")
lines.append("")
lines.append("    public string NoProfileSelected() => IsEnglish ? \"No profile selected\" : \"未选择订阅\";")
lines.append("")
lines.append("    public string NotSelected() => IsEnglish ? \"(none)\" : \"（未选择）\";")
lines.append("")
lines.append("    public string DeleteProfileConfirm(string name) => IsEnglish")
lines.append("        ? $\"Delete \\\"{name}\\\"? Its config file will be deleted too.\"")
lines.append("        : $\"确定删除「{name}」吗？该操作会同时删除其配置文件。\";")
lines.append("")
lines.append("    public string ProfileSummary(string name, string nodes) => IsEnglish ? $\"Profile: {name} ({nodes})\" : $\"订阅：{name}（{nodes}）\";")
lines.append("")
lines.append("    public string ConfigSummary(string file) => IsEnglish ? $\"Config: {file}\" : $\"配置：{file}\";")
lines.append("")
lines.append("    public string NodesCountText(int count) => IsEnglish ? $\"{count} nodes\" : $\"{count} 个节点\";")
lines.append("")
lines.append("    // ==================== 强类型词条 ====================")
for k, z, e in TABLE:
    lines.append("")
    lines.append(f"    /// <summary>{z}</summary>")
    lines.append(f"    public string {k} => T(nameof({k}));")
lines.append("}")

pathlib.Path("Services/LocalizationService.cs").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"LocalizationService.cs 已生成：{len(TABLE)} 条词条")

# ============================================================ 2) 替换 XAML
files = ["MainWindow.xaml"] + [str(p) for p in sorted(pathlib.Path("Views").glob("*.xaml"))]
attr_re = re.compile(r'([\w][\w.:]*)(\s*=\s*)"([^"]*)"')
leftovers = []

for f in files:
    p = pathlib.Path(f)
    text = p.read_text(encoding="utf-8")
    hits = 0

    def repl(m):
        global hits
        name, eq, val = m.group(1), m.group(2), m.group(3)
        if val.startswith("{"):
            return m.group(0)
        key = ZH2KEY.get(val)
        if key is None:
            return m.group(0)
        hits += 1
        return f'{name}{eq}"{{x:Bind Loc.{key}, Mode=OneWay}}"'

    new = attr_re.sub(repl, text)
    if new != text:
        p.write_text(new, encoding="utf-8")
    print(f"{f}: 替换 {hits} 处")

# 找出还没被替换的中文
cjk = re.compile(r'[\u4e00-\u9fff]')
for f in files:
    for i, line in enumerate(pathlib.Path(f).read_text(encoding="utf-8").splitlines(), 1):
        if cjk.search(line) and not line.strip().startswith("<!--"):
            leftovers.append(f"{f}:{i}  {line.strip()[:110]}")

print("\n===== 残留中文（需人工处理）=====")
for l in leftovers:
    print(l)
print("合计", len(leftovers))
