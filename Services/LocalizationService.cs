using CommunityToolkit.Mvvm.ComponentModel;

namespace SingBoxWinUI.Services;

/// <summary>
/// 响应式本地化服务。
/// 不走 ApplicationLanguages / .resw：Unpackaged 模式下 PrimaryLanguageOverride 无效，
/// 而且 resw 切换必须重启。这里把所有词条做成强类型属性，切语言时逐个 PropertyChanged，
/// 界面上所有 Mode=OneWay 的 x:Bind 会当场重算，无需重启。
/// </summary>
public sealed class LocalizationService : ObservableObject
{
    public const string ChineseLanguage = "zh-CN";
    public const string EnglishLanguage = "en-US";

    public static LocalizationService Instance { get; } = new();

    private static readonly Dictionary<string, (string Zh, string En)> Strings = new(StringComparer.Ordinal)
    {
        ["NavOverview"] = ("概览", "Overview"),
        ["NavProxies"] = ("节点策略", "Proxies"),
        ["NavConnections"] = ("活动连接", "Connections"),
        ["NavLogs"] = ("运行日志", "Logs"),
        ["NavProfiles"] = ("订阅管理", "Profiles"),
        ["NavSettings"] = ("设置", "Settings"),
        ["SidebarMemory"] = ("内存", "Mem"),
        ["TrayShowWindow"] = ("显示主界面", "Show Window"),
        ["TrayStartCore"] = ("启动内核", "Start Core"),
        ["TrayStopCore"] = ("停止内核", "Stop Core"),
        ["TrayExit"] = ("彻底退出", "Exit"),
        ["TrayModeRule"] = ("规则 (Rule)", "Rule"),
        ["TrayModeGlobal"] = ("全局 (Global)", "Global"),
        ["TrayModeDirect"] = ("直连 (Direct)", "Direct"),
        ["CommonOk"] = ("确定", "OK"),
        ["CommonCancel"] = ("取消", "Cancel"),
        ["CommonSave"] = ("保存", "Save"),
        ["CommonDelete"] = ("删除", "Delete"),
        ["CommonRestoreDefault"] = ("恢复默认", "Restore Default"),
        ["CommonMinutes1"] = ("1 分钟", "1 min"),
        ["CommonMinutes3"] = ("3 分钟", "3 min"),
        ["CommonMinutes5"] = ("5 分钟", "5 min"),
        ["CommonMinutes10"] = ("10 分钟", "10 min"),
        ["CommonMinutes15"] = ("15 分钟", "15 min"),
        ["CommonMinutes30"] = ("30 分钟", "30 min"),
        ["CommonMinutes60"] = ("60 分钟", "60 min"),
        ["CommonHour1"] = ("每 1 小时", "Every hour"),
        ["CommonHour6"] = ("每 6 小时", "Every 6 hours"),
        ["CommonHour12"] = ("每 12 小时", "Every 12 hours"),
        ["CommonHour24"] = ("每 24 小时", "Every 24 hours"),
        ["CommonNever"] = ("不自动更新", "Never"),
        ["StatusRunning"] = ("运行中", "Running"),
        ["StatusStopped"] = ("未运行", "Stopped"),
        ["HomeManageProfiles"] = ("切换/管理订阅", "Manage Profiles"),
        ["HomeToggleCore"] = ("启停 sing-box 内核", "Start or stop the sing-box core"),
        ["HomeCurrentProfile"] = ("当前订阅", "Current Profile"),
        ["HomeUpdateProfile"] = ("更新订阅", "Update"),
        ["HomeUpdatedAt"] = ("更新时间", "Updated"),
        ["HomeExpiresAt"] = ("到期时间", "Expires"),
        ["HomeUsedTotal"] = ("已用 / 总流量", "Used / Total"),
        ["HomeUsageA11y"] = ("订阅流量使用比例", "Subscription usage"),
        ["HomeRemoteProfile"] = ("在线订阅", "Remote"),
        ["HomeLocalProfile"] = ("本地配置", "Local"),
        ["HomeTunAdminHint"] = ("TUN 需要管理员权限，否则内核无法创建网卡。", "Administrator privileges are required for TUN mode, otherwise the core cannot create the adapter."),
        ["HomeRestartAsAdmin"] = ("以管理员身份重启（TUN 必需）", "Restart as Admin"),
        ["HomeDismiss"] = ("关闭提示", "Dismiss"),
        ["HomeAppIcon"] = ("应用图标", "Application icon"),
        ["HomeProxyMode"] = ("代理模式", "Proxy Mode"),
        ["HomeProxyModeHint"] = ("运行中切换立即生效", "Applies immediately while running"),
        ["HomeModeRule"] = ("规则", "Rule"),
        ["HomeModeGlobal"] = ("全局", "Global"),
        ["HomeModeDirect"] = ("直连", "Direct"),
        ["HomeModeRuleA11y"] = ("规则模式", "Rule mode"),
        ["HomeModeGlobalA11y"] = ("全局模式", "Global mode"),
        ["HomeModeDirectA11y"] = ("直连模式", "Direct mode"),
        ["HomeNetworkSettings"] = ("网络设置", "Network Settings"),
        ["HomeSystemProxy"] = ("系统代理", "System Proxy"),
        ["HomeSystemProxyA11y"] = ("启用系统代理", "Enable system proxy"),
        ["HomeTunMode"] = ("TUN 模式", "TUN Mode"),
        ["HomeTunModeA11y"] = ("启用 TUN 模式", "Enable TUN mode"),
        ["ProxiesRefresh"] = ("刷新节点", "Refresh"),
        ["ProxiesTestLatency"] = ("一键测速", "Test Latency"),
        ["ProxiesTesting"] = ("测速中", "Testing"),
        ["ProxiesSortDefault"] = ("排序: 默认", "Sort: Default"),
        ["ProxiesSortDelay"] = ("排序: 延迟", "Sort: Delay"),
        ["ProxiesSortName"] = ("排序: 名称", "Sort: Name"),
        ["ProxiesToggleSort"] = ("切换升序 / 降序", "Toggle ascending / descending"),
        ["ProxiesToggleSortA11y"] = ("切换升降序", "Toggle sort order"),
        ["ProxiesClickCardHint"] = ("点击卡片切换节点", "Click a card to switch node"),
        ["ProxiesStartCoreFirst"] = ("请先在「概览与日志」中启动内核", "Start the core on the Overview page first"),
        ["ProfilesNewName"] = ("订阅名称", "Profile name"),
        ["ProfilesNewNameA11y"] = ("新订阅名称", "New profile name"),
        ["ProfilesNewUrl"] = ("订阅链接（https://...）", "Subscription URL (https://...)"),
        ["ProfilesNewUrlA11y"] = ("新订阅链接", "New subscription URL"),
        ["ProfilesImportRemote"] = ("导入在线订阅", "Import Remote"),
        ["ProfilesImportLocal"] = ("导入本地文件", "Import Local File"),
        ["ProfilesUpdateAll"] = ("更新全部订阅", "Update All"),
        ["ProfilesUpdateNow"] = ("立即更新", "Update"),
        ["ProfilesEnable"] = ("启用此订阅", "Enable this profile"),
        ["ProfilesInUse"] = ("当前使用", "In use"),
        ["ProfilesRenameEdit"] = ("重命名/编辑", "Rename / Edit"),
        ["ProfilesInterval"] = ("定时更新", "Auto update"),
        ["ProfilesIntervalA11y"] = ("定时更新周期", "Update interval"),
        ["ProfilesMixinTitle"] = ("全局拓展配置（Mixin）", "Global Mixin"),
        ["ProfilesPreMixin"] = ("全局前置拓展", "Pre-mixin"),
        ["ProfilesPostMixin"] = ("全局后置拓展", "Post-mixin"),
        ["ProfilesPreMixinHint"] = ("覆盖订阅默认字段，并将规则/节点前置插入（高优先级抢占）", "Overrides subscription fields and prepends rules / nodes (higher priority)."),
        ["ProfilesPostMixinHint"] = ("在订阅配置末尾追加自定义规则、规则集或自建节点", "Appends custom rules, rule-sets or nodes to the end of the subscription config."),
        ["ProfilesEditPreJson"] = ("编辑前置 JSON", "Edit pre-mixin JSON"),
        ["ProfilesEditPostJson"] = ("编辑后置 JSON", "Edit post-mixin JSON"),
        ["ProfilesEnablePreA11y"] = ("启用全局前置拓展", "Enable pre-mixin"),
        ["ProfilesEnablePostA11y"] = ("启用全局后置拓展", "Enable post-mixin"),
        ["ProfilesEditTitle"] = ("编辑订阅", "Edit Profile"),
        ["ProfilesDeleteTitle"] = ("删除订阅", "Delete Profile"),
        ["ProfilesPreMixinJson"] = ("前置拓展 JSON", "Pre-mixin JSON"),
        ["ProfilesPostMixinJson"] = ("后置拓展 JSON", "Post-mixin JSON"),
        ["ProfilesOpenWithEditor"] = ("用系统默认编辑器打开文件", "Open with the system default editor"),
        ["ProfilesEditPreMixinTitle"] = ("编辑全局前置拓展", "Edit Global Pre-mixin"),
        ["ProfilesEditPostMixinTitle"] = ("编辑全局后置拓展", "Edit Global Post-mixin"),
        ["ProfilesLocalUrlReadonly"] = ("本地配置不可修改链接", "A local profile's URL cannot be changed"),
        ["ConnPause"] = ("暂停刷新", "Pause"),
        ["ConnResume"] = ("继续刷新", "Resume"),
        ["ConnShowColumns"] = ("显示参数", "Columns"),
        ["ConnCloseAll"] = ("全部断开", "Close All"),
        ["ConnSortByA11y"] = ("排序维度", "Sort by"),
        ["ConnSearchA11y"] = ("连接搜索", "Connection search"),
        ["ConnSearchPlaceholder"] = ("搜索 域名 / IP / 进程 / 规则 / 出站", "Search host / IP / process / rule / outbound"),
        ["ConnColHost"] = ("目标域名", "Host"),
        ["ConnColDestinationIp"] = ("目标 IP 地址", "Destination IP"),
        ["ConnColProcess"] = ("进程名称", "Process"),
        ["ConnColRule"] = ("命中路由规则", "Rule"),
        ["ConnColChain"] = ("出站节点链路", "Outbound chain"),
        ["ConnOutboundFilterA11y"] = ("出站筛选", "Outbound filter"),
        ["ConnColInbound"] = ("入站与协议", "Inbound"),
        ["ConnColOutboundGroup"] = ("出站策略组", "Outbound group"),
        ["ConnColStart"] = ("建立时间", "Started"),
        ["ConnColDuration"] = ("连接持续时长", "Duration"),
        ["ConnColUploadRate"] = ("上传速度", "Upload"),
        ["ConnColDownloadRate"] = ("下载速度", "Download"),
        ["ConnColUploadTotal"] = ("上传总流量", "Uploaded"),
        ["ConnColDownloadTotal"] = ("下载总流量", "Downloaded"),
        ["ConnColTrafficTotal"] = ("累计上下行流量", "Total traffic"),
        ["ConnColSpeedTotal"] = ("实时上下行速度", "Current speed"),
        ["ConnCloseA11y"] = ("关闭连接", "Close connection"),
        ["ConnEmptyFiltered"] = ("没有匹配的连接", "No matching connections"),
        ["ConnEmpty"] = ("暂无活动连接", "No active connections"),
        ["LogsSearchPlaceholder"] = ("搜索日志内容、规则或节点...", "Search log text, rules or nodes..."),
        ["LogsSearchA11y"] = ("日志搜索", "Log search"),
        ["LogsLevelA11y"] = ("日志级别筛选", "Log level filter"),
        ["LogsLevelAll"] = ("全部级别", "All levels"),
        ["LogsLevelInfoUp"] = ("仅 Info 及以上", "Info and above"),
        ["LogsLevelWarnUp"] = ("仅 Warning 及以上", "Warning and above"),
        ["LogsLevelError"] = ("仅 Error", "Error only"),
        ["LogsAutoScroll"] = ("自动滚动", "Auto scroll"),
        ["LogsCopyAllA11y"] = ("复制全部日志", "Copy all logs"),
        ["LogsCopy"] = ("复制", "Copy"),
        ["LogsExportA11y"] = ("导出日志", "Export logs"),
        ["LogsExport"] = ("导出文件", "Export"),
        ["LogsClearA11y"] = ("清空日志", "Clear logs"),
        ["LogsClear"] = ("清空", "Clear"),
        ["LogsEmpty"] = ("暂无日志", "No logs yet"),
        ["SetSectionStartup"] = ("启动与后台", "Startup & Background"),
        ["SetSectionGeneral"] = ("通用与网络", "General & Network"),
        ["SetSectionTun"] = ("TUN 虚拟网卡设置", "TUN Settings"),
        ["SetSectionBackup"] = ("备份与恢复", "Backup & Restore"),
        ["SetSectionAdvanced"] = ("高级与目录", "Advanced"),
        ["SetLanguageLabel"] = ("语言 / Language", "Language"),
        ["SetLanguageHint"] = ("切换后立即生效，无需重启", "Applies immediately, no restart needed"),
        ["SetLanguageChinese"] = ("简体中文", "简体中文"),
        ["SetLanguageEnglish"] = ("English", "English"),
        ["SetAppearance"] = ("外观主题", "Theme"),
        ["SetThemeSystem"] = ("跟随系统", "System"),
        ["SetThemeLight"] = ("浅色", "Light"),
        ["SetThemeDark"] = ("深色", "Dark"),
        ["SetAutoStartApp"] = ("开机自动启动", "Launch at sign-in"),
        ["SetAutoStartAppHint"] = ("登录 Windows 后自动启动客户端", "Start the app automatically after you sign in to Windows."),
        ["SetSilentTray"] = ("自启时静默驻留托盘", "Start silently in tray"),
        ["SetSilentTrayHint"] = ("开机启动时不显示主窗口，直接缩到托盘", "Do not show the main window at startup; go straight to the tray."),
        ["SetAutoStartCore"] = ("打开软件时自动启动内核", "Start core on launch"),
        ["SetAutoStartCoreHint"] = ("客户端启动后自动拉起 sing-box", "Bring up sing-box automatically when the app starts."),
        ["SetCloseToTray"] = ("关闭主窗口时隐藏到系统托盘", "Close to tray"),
        ["SetCloseToTrayHint"] = ("关闭时保持后台运行；从托盘菜单「彻底退出」才会真正结束", "Keeps running in the background; use \"Exit\" in the tray menu to really quit."),
        ["SetLightweight"] = ("后台轻量模式", "Background lightweight mode"),
        ["SetLightweightHint"] = ("隐藏到系统托盘达到指定时间后，自动暂停 UI 遥测轮询、释放非活动页面缓存并压缩进程内存工作集", "After the window stays hidden in the tray for the given time, UI telemetry polling pauses, inactive page caches are released and the working set is trimmed."),
        ["SetLightweightDelayA11y"] = ("轻量模式触发时间", "Lightweight mode delay"),
        ["SetAutoStartAppAdminHint"] = ("当前非管理员，仅创建普通权限自启；若需开机自动开启 TUN，请先提权后再开启此开关。", "Running without administrator rights, so auto-start uses normal privileges. To start TUN at boot, elevate first and then enable this switch."),
        ["SetIpv6"] = ("启用 IPv6 支持", "Enable IPv6"),
        ["SetIpv6Hint"] = ("控制 DNS 解析策略与 TUN 虚拟网卡的 IPv6 双栈支持（修改后若内核正在运行将自动重启生效）", "Controls the DNS strategy and IPv6 dual-stack support for the TUN adapter (the core restarts automatically if it is running)."),
        ["SetUnifiedDelay"] = ("统一延迟测试", "Unified latency test"),
        ["SetUnifiedDelayHint"] = ("开启后将连续进行两次延迟测试并取第二次结果，消除不同协议首次连接握手带来的延迟偏差", "Runs two latency probes and keeps the second result, removing first-connection handshake bias between protocols."),
        ["SetSpeedTestUrl"] = ("节点测速地址", "Latency test URL"),
        ["SetSpeedTestUrlHint"] = ("用于策略组「一键测速」的探测地址，需返回 2xx", "Probe URL used by \"Test Latency\"; it must return 2xx."),
        ["SetAutoTestDelay"] = ("后台自动检测延迟", "Auto latency test"),
        ["SetAutoTestDelayHint"] = ("内核运行期间按固定周期静默重测策略组延迟，并按当前排序规则刷新顺序", "Silently re-tests group latency on a fixed interval while the core runs and re-applies the current sort order."),
        ["SetAutoTestIntervalA11y"] = ("自动测速间隔", "Auto test interval"),
        ["SetTunMtuHint"] = ("默认 9000，链路异常时可尝试降到 1500", "Defaults to 9000; lower it to 1500 if the link misbehaves."),
        ["SetTunInterface"] = ("网卡名称", "Interface name"),
        ["SetTunInterfaceHint"] = ("写入 interface_name，留空则由内核自动命名", "Written to interface_name; leave empty to let the core pick a name."),
        ["SetTunInterfaceA11y"] = ("TUN 网卡名称", "TUN interface name"),
        ["SetTunStack"] = ("网络栈", "Network stack"),
        ["SetTunStackHint"] = ("system 走系统协议栈，gvisor 兼容性更好，mixed 由内核自动选择", "system uses the OS stack, gvisor is more compatible, mixed lets the core decide."),
        ["SetTunStackA11y"] = ("TUN 网络栈", "TUN network stack"),
        ["SetTunAutoRoute"] = ("自动配置路由", "Auto route"),
        ["SetTunAutoRouteHint"] = ("auto_route，接管系统路由表", "auto_route: takes over the system routing table."),
        ["SetTunAutoRouteA11y"] = ("TUN 自动配置路由", "TUN auto route"),
        ["SetTunStrictRoute"] = ("严格路由", "Strict route"),
        ["SetTunStrictRouteHint"] = ("strict_route，防止流量绕过 TUN 造成泄漏", "strict_route: prevents traffic from bypassing the TUN adapter."),
        ["SetTunStrictRouteA11y"] = ("TUN 严格路由", "TUN strict route"),
        ["SetTunApply"] = ("应用并重启 TUN", "Apply and restart TUN"),
        ["SetTunApplyHint"] = ("内核正以 TUN 运行时，点右侧按钮会按新参数热重启。", "When the core is running with TUN, the button on the right hot-restarts it with the new settings."),
        ["SetAppliesImmediately"] = ("修改后立即生效", "Applies immediately"),
        ["SetCoreVersion"] = ("sing-box 内核版本", "sing-box version"),
        ["SetCoreDirectory"] = ("配置与内核目录", "Config & core directory"),
        ["SetCoreDirectoryHint"] = ("config.json、sing-box.exe 与运行配置都在这里", "config.json, sing-box.exe and the runtime config live here."),
        ["SetOpenDirectory"] = ("打开目录", "Open folder"),
        ["SetRuleSets"] = ("规则集（Rule-sets）管理与更新", "Rule-sets"),
        ["SetRuleSetsAutoUpdate"] = ("自动更新", "Auto update"),
        ["SetRuleSetsAutoUpdateA11y"] = ("自动更新规则集", "Auto update rule-sets"),
        ["SetRuleSetsUpdateAll"] = ("立即更新全部", "Update All"),
        ["SetRuleSetsUpdateAllA11y"] = ("立即更新全部规则集", "Update all rule-sets"),
        ["SetRuleSetsUrl"] = ("规则集下载地址", "Rule-set base URL"),
        ["SetRuleSetsUrlPlaceholder"] = ("规则集下载地址模板，支持 {category} {short} {name} {file}", "Rule-set URL template; supports {category} {short} {name} {file}"),
        ["SetRuleSetsUrlReset"] = ("恢复默认规则集地址", "Restore default rule-set URL"),
        ["SetRuleSetsList"] = ("查看规则集文件列表", "Show rule-set files"),
        ["SetBackupHint"] = ("备份包含客户端设置、全部订阅配置与拓展配置（不含内核程序与缓存）", "Backups contain app settings, all profiles and mixin configs (no core binary or cache)."),
        ["SetExportBackup"] = ("导出本地备份", "Export Backup"),
        ["SetRestoreLocal"] = ("从本地备份恢复", "Restore Local"),
        ["SetAutoWebDav"] = ("启动时自动 WebDAV 备份", "Auto WebDAV backup on start"),
        ["SetAutoWebDavHint"] = ("每次启动后在后台静默上传一次，失败只记日志不打扰", "Uploads once in the background after each start; failures are only logged."),
        ["SetWebDavUrl"] = ("WebDAV 地址", "WebDAV URL"),
        ["SetWebDavUrlPlaceholder"] = ("WebDAV 地址（https://dav.jianguoyun.com/dav/SingBoxBackup/）", "WebDAV URL (https://dav.jianguoyun.com/dav/SingBoxBackup/)"),
        ["SetWebDavUser"] = ("WebDAV 用户名", "WebDAV username"),
        ["SetWebDavPassword"] = ("WebDAV 密码", "WebDAV password"),
        ["SetTestConnection"] = ("测试连接", "Test"),
        ["SetTestConnectionA11y"] = ("测试 WebDAV 连接", "Test WebDAV connection"),
        ["SetWebDavBackupNow"] = ("立即 WebDAV 备份", "Backup Now"),
        ["SetRestoreWebDav"] = ("从 WebDAV 恢复", "Restore from WebDAV"),
        ["SetRestoreConfirm"] = ("恢复备份将覆盖当前的客户端设置、所有订阅及拓展配置，是否继续？", "Restoring a backup overwrites the current app settings, all profiles and mixin configs. Continue?"),
        ["SetRestoreConfirmButton"] = ("确认恢复", "Restore"),
        ["SetBackupFileType"] = ("备份包", "Backup archive"),
        ["ConnCardTitle"] = ("网络连通性与出口信息", "Connectivity & Exit IP"),
        ["ConnExitIp"] = ("出口 IP", "Exit IP"),
        ["ConnRegionIsp"] = ("地区 / 运营商", "Region / ISP"),
        ["ConnChecking"] = ("检测中…", "Checking…"),
        ["ConnRecheck"] = ("立即检测", "Check now"),
        ["ConnShowIp"] = ("显示 IP", "Show IP"),
        ["ConnHideIp"] = ("隐藏 IP", "Hide IP"),
        ["ConnCopyIp"] = ("复制 IP", "Copy IP"),
        ["ConnNotConnected"] = ("未连接", "Not connected"),
        ["ConnCoreStopped"] = ("内核未启动", "Core is not running"),
        ["ConnFailed"] = ("检测失败", "Check failed"),
        ["ConnTimeout"] = ("超时", "Timeout"),
        ["ConnViaProxy"] = ("经本地代理通道", "Via local proxy"),
        ["ConnViaDirect"] = ("直连探测", "Direct connection"),
        ["ConnCopied"] = ("已复制出口 IP", "Exit IP copied"),
        ["ConnUnknownRegion"] = ("未知地区", "Unknown region"),
        ["ConnNoIp"] = ("--", "--"),
        ["ConnIpMaskA11y"] = ("切换 IP 显示", "Toggle IP visibility"),
        ["ConnLatencyA11y"] = ("连通性延迟", "Connectivity latency"),
        ["ConnGoogle"] = ("Google", "Google"),
        ["ConnGithub"] = ("GitHub", "GitHub"),
        ["ConnCloudflare"] = ("Cloudflare", "Cloudflare"),
        ["SingBoxVersion"] = ("sing-box 版本", "sing-box version"),
        ["KernelCardTitle"] = ("sing-box 内核版本", "sing-box Kernel"),
        ["KernelCurrentVersion"] = ("当前版本", "Current version"),
        ["KernelCheckUpdate"] = ("检查更新", "Check for updates"),
        ["KernelUpdateNow"] = ("立即更新", "Update now"),
        ["KernelChangelog"] = ("查看更新日志", "View changelog"),
        ["KernelUpToDate"] = ("已是最新版本", "Up to date"),
        ["KernelNotInstalled"] = ("未安装", "Not installed"),
        ["KernelChecking"] = ("正在检查更新…", "Checking for updates…"),
        ["KernelCheckFailed"] = ("检查更新失败", "Update check failed"),
        ["KernelNotChecked"] = ("尚未检查更新", "Not checked yet"),
        ["KernelDownloading"] = ("正在下载内核…", "Downloading core…"),
        ["KernelInstalling"] = ("正在替换内核…", "Replacing core…"),
        ["KernelRestarting"] = ("正在重启内核…", "Restarting core…"),
        ["KernelUpdated"] = ("内核已更新", "Core updated"),
        ["KernelUpdateFailed"] = ("更新失败", "Update failed"),
        ["KernelRolledBack"] = ("已回滚到旧内核", "Rolled back to previous core"),
        ["KernelNoAsset"] = ("未找到当前架构的发行包", "No release asset for this architecture"),
        ["KernelLightweightBlocked"] = ("后台轻量模式下不自动检查更新", "No automatic update check in lightweight mode"),
        ["KernelUpdateHint"] = ("更新会短暂停机换核，内核运行中会自动恢复连接", "Updating briefly stops the core; connections resume automatically"),
        ["KernelUpdateConfirmTitle"] = ("更新内核", "Update core"),
        ["KernelUpdateConfirm"] = ("将下载并替换 Core\\sing-box.exe，是否继续？", "Download and replace Core\\sing-box.exe. Continue?"),
        ["KernelAssetSize"] = ("下载大小", "Download size"),
        ["KernelValidateFailed"] = ("新内核预校验失败，已中止更新", "New core failed pre-validation; update aborted"),
        ["PickerAllFiles"] = ("所有文件 (*.*)", "All files (*.*)"),
        ["PickerOpenTitle"] = ("选择文件", "Select a file"),
        ["PickerSaveTitle"] = ("保存文件", "Save file"),
        ["PickerFailedTitle"] = ("文件对话框打开失败", "File dialog failed"),
        ["ProfilesImportFailedTitle"] = ("导入失败", "Import failed"),
        ["ProfilesMixinReload"] = ("重新读取文件", "Reload from disk"),
        ["AboutCardTitle"] = ("关于", "About"),
        ["AboutGitHub"] = ("GitHub 主页", "GitHub"),
        ["AboutCheckUpdate"] = ("检查更新", "Check for updates"),
        ["AboutChecking"] = ("正在检查更新…", "Checking for updates…"),
        ["AboutDownload"] = ("前往下载", "Download"),
        ["AboutAutoCheck"] = ("自动检查更新", "Automatically check for updates"),
        ["AboutAutoCheckHint"] = ("启动应用时自动检测客户端新版本", "Automatically check for new client releases on startup"),
        ["AboutNotChecked"] = ("尚未检查更新", "Not checked yet"),
        ["AboutUpToDate"] = ("已是最新版本", "Up to date"),
        ["AboutCheckFailed"] = ("检查更新失败", "Update check failed"),
        ["AboutLightweightBlocked"] = ("后台轻量模式下不自动检查更新", "No automatic update check in lightweight mode"),
        ["StatusRunningProxy"] = ("运行中 (系统代理)", "Running (System Proxy)"),
        ["StatusRunningTun"] = ("运行中 (TUN)", "Running (TUN)"),
        ["StatusRunningBoth"] = ("运行中 (系统代理 + TUN)", "Running (System Proxy + TUN)"),
        ["HomeSwitchHint"] = ("开启任一开关即启动内核，全部关闭即停止内核", "Turn on either switch to start the core; turn both off to stop it"),
    };

    /// <summary>所有词条名，切语言时逐个通知。</summary>
    private static readonly string[] AllKeys =
    [
        "NavOverview", "NavProxies", "NavConnections", "NavLogs", "NavProfiles", "NavSettings",
        "SidebarMemory", "TrayShowWindow", "TrayStartCore", "TrayStopCore", "TrayExit", "TrayModeRule",
        "TrayModeGlobal", "TrayModeDirect", "CommonOk", "CommonCancel", "CommonSave", "CommonDelete",
        "CommonRestoreDefault", "CommonMinutes1", "CommonMinutes3", "CommonMinutes5", "CommonMinutes10", "CommonMinutes15",
        "CommonMinutes30", "CommonMinutes60", "CommonHour1", "CommonHour6", "CommonHour12", "CommonHour24",
        "CommonNever", "StatusRunning", "StatusStopped", "HomeManageProfiles", "HomeToggleCore", "HomeCurrentProfile",
        "HomeUpdateProfile", "HomeUpdatedAt", "HomeExpiresAt", "HomeUsedTotal", "HomeUsageA11y", "HomeRemoteProfile",
        "HomeLocalProfile", "HomeTunAdminHint", "HomeRestartAsAdmin", "HomeDismiss", "HomeAppIcon", "HomeProxyMode",
        "HomeProxyModeHint", "HomeModeRule", "HomeModeGlobal", "HomeModeDirect", "HomeModeRuleA11y", "HomeModeGlobalA11y",
        "HomeModeDirectA11y", "HomeNetworkSettings", "HomeSystemProxy", "HomeSystemProxyA11y", "HomeTunMode", "HomeTunModeA11y",
        "ProxiesRefresh", "ProxiesTestLatency", "ProxiesTesting", "ProxiesSortDefault", "ProxiesSortDelay", "ProxiesSortName",
        "ProxiesToggleSort", "ProxiesToggleSortA11y", "ProxiesClickCardHint", "ProxiesStartCoreFirst", "ProfilesNewName", "ProfilesNewNameA11y",
        "ProfilesNewUrl", "ProfilesNewUrlA11y", "ProfilesImportRemote", "ProfilesImportLocal", "ProfilesUpdateAll", "ProfilesUpdateNow",
        "ProfilesEnable", "ProfilesInUse", "ProfilesRenameEdit", "ProfilesInterval", "ProfilesIntervalA11y", "ProfilesMixinTitle",
        "ProfilesPreMixin", "ProfilesPostMixin", "ProfilesPreMixinHint", "ProfilesPostMixinHint", "ProfilesEditPreJson", "ProfilesEditPostJson",
        "ProfilesEnablePreA11y", "ProfilesEnablePostA11y", "ProfilesEditTitle", "ProfilesDeleteTitle", "ProfilesPreMixinJson", "ProfilesPostMixinJson",
        "ProfilesOpenWithEditor", "ProfilesEditPreMixinTitle", "ProfilesEditPostMixinTitle", "ProfilesLocalUrlReadonly", "ConnPause", "ConnResume",
        "ConnShowColumns", "ConnCloseAll", "ConnSortByA11y", "ConnSearchA11y", "ConnSearchPlaceholder", "ConnColHost",
        "ConnColDestinationIp", "ConnColProcess", "ConnColRule", "ConnColChain", "ConnOutboundFilterA11y", "ConnColInbound",
        "ConnColOutboundGroup", "ConnColStart", "ConnColDuration", "ConnColUploadRate", "ConnColDownloadRate", "ConnColUploadTotal",
        "ConnColDownloadTotal", "ConnColTrafficTotal", "ConnColSpeedTotal", "ConnCloseA11y", "ConnEmptyFiltered", "ConnEmpty",
        "LogsSearchPlaceholder", "LogsSearchA11y", "LogsLevelA11y", "LogsLevelAll", "LogsLevelInfoUp", "LogsLevelWarnUp",
        "LogsLevelError", "LogsAutoScroll", "LogsCopyAllA11y", "LogsCopy", "LogsExportA11y", "LogsExport",
        "LogsClearA11y", "LogsClear", "LogsEmpty", "SetSectionStartup", "SetSectionGeneral", "SetSectionTun",
        "SetSectionBackup", "SetSectionAdvanced", "SetLanguageLabel", "SetLanguageHint", "SetLanguageChinese", "SetLanguageEnglish",
        "SetAppearance", "SetThemeSystem", "SetThemeLight", "SetThemeDark", "SetAutoStartApp", "SetAutoStartAppHint",
        "SetSilentTray", "SetSilentTrayHint", "SetAutoStartCore", "SetAutoStartCoreHint", "SetCloseToTray", "SetCloseToTrayHint",
        "SetLightweight", "SetLightweightHint", "SetLightweightDelayA11y", "SetAutoStartAppAdminHint", "SetIpv6", "SetIpv6Hint",
        "SetUnifiedDelay", "SetUnifiedDelayHint", "SetSpeedTestUrl", "SetSpeedTestUrlHint", "SetAutoTestDelay", "SetAutoTestDelayHint",
        "SetAutoTestIntervalA11y", "SetTunMtuHint", "SetTunInterface", "SetTunInterfaceHint", "SetTunInterfaceA11y", "SetTunStack",
        "SetTunStackHint", "SetTunStackA11y", "SetTunAutoRoute", "SetTunAutoRouteHint", "SetTunAutoRouteA11y", "SetTunStrictRoute",
        "SetTunStrictRouteHint", "SetTunStrictRouteA11y", "SetTunApply", "SetTunApplyHint", "SetAppliesImmediately", "SetCoreVersion",
        "SetCoreDirectory", "SetCoreDirectoryHint", "SetOpenDirectory", "SetRuleSets", "SetRuleSetsAutoUpdate", "SetRuleSetsAutoUpdateA11y",
        "SetRuleSetsUpdateAll", "SetRuleSetsUpdateAllA11y", "SetRuleSetsUrl", "SetRuleSetsUrlPlaceholder", "SetRuleSetsUrlReset", "SetRuleSetsList",
        "SetBackupHint", "SetExportBackup", "SetRestoreLocal", "SetAutoWebDav", "SetAutoWebDavHint", "SetWebDavUrl",
        "SetWebDavUrlPlaceholder", "SetWebDavUser", "SetWebDavPassword", "SetTestConnection", "SetTestConnectionA11y", "SetWebDavBackupNow",
        "SetRestoreWebDav", "SetRestoreConfirm", "SetRestoreConfirmButton", "SetBackupFileType",
        "ConnCardTitle", "ConnExitIp", "ConnRegionIsp", "ConnChecking", "ConnRecheck", "ConnShowIp",
        "ConnHideIp", "ConnCopyIp", "ConnNotConnected", "ConnCoreStopped", "ConnFailed", "ConnTimeout",
        "ConnViaProxy", "ConnViaDirect", "ConnCopied", "ConnUnknownRegion", "ConnNoIp", "ConnIpMaskA11y",
        "ConnLatencyA11y", "ConnGoogle", "ConnGithub", "ConnCloudflare", "SingBoxVersion", "KernelCardTitle", "KernelCurrentVersion",
        "KernelCheckUpdate", "KernelUpdateNow", "KernelChangelog", "KernelUpToDate", "KernelNotInstalled", "KernelChecking",
        "KernelCheckFailed", "KernelNotChecked", "KernelDownloading", "KernelInstalling", "KernelRestarting", "KernelUpdated",
        "KernelUpdateFailed", "KernelRolledBack", "KernelNoAsset", "KernelLightweightBlocked", "KernelUpdateHint", "KernelUpdateConfirmTitle",
        "KernelUpdateConfirm", "KernelAssetSize", "KernelValidateFailed",
        "PickerAllFiles", "PickerOpenTitle", "PickerSaveTitle", "PickerFailedTitle", "ProfilesImportFailedTitle", "ProfilesMixinReload",
        "AboutCardTitle", "AboutGitHub", "AboutCheckUpdate", "AboutChecking", "AboutDownload", "AboutAutoCheck", "AboutAutoCheckHint", "AboutNotChecked", "AboutUpToDate", "AboutCheckFailed", "AboutLightweightBlocked",
        "StatusRunningProxy", "StatusRunningTun", "StatusRunningBoth", "HomeSwitchHint",
    ];

    private string _currentLanguage = ChineseLanguage;

    private LocalizationService()
    {
    }

    /// <summary>当前语言：zh-CN 或 en-US。</summary>
    public string CurrentLanguage => _currentLanguage;

    public bool IsEnglish => string.Equals(_currentLanguage, EnglishLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>语言变化后触发，供 ViewModel 的拼接文案与托盘菜单刷新。</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>切换语言；语言没变时直接返回，避免无谓的全量刷新。</summary>
    public void SetLanguage(string? language)
    {
        var normalized = string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
            ? EnglishLanguage
            : ChineseLanguage;

        if (string.Equals(normalized, _currentLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _currentLanguage = normalized;

        // 逐条通知：x:Bind 生成的是按属性名分派的 switch，空字符串不一定能命中，
        // 所以这里既发空串（覆盖支持"全部刷新"的实现）也逐条发一遍。
        OnPropertyChanged(string.Empty);
        foreach (var key in AllKeys)
        {
            OnPropertyChanged(key);
        }

        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(IsEnglish));

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按词条名取当前语言文案，找不到时回退成 key 本身。</summary>
    public string T(string key)
    {
        if (!Strings.TryGetValue(key, out var pair))
        {
            return key;
        }

        return IsEnglish ? pair.En : pair.Zh;
    }

    public string this[string key] => T(key);

    /// <summary>
    /// 临时状态文案的双语取值。这类字符串只显示一次、不需要跟随语言实时刷新，
    /// 与其为每句话都在词条表里占一行，不如就地给两种语言。
    /// </summary>
    public string Say(string zh, string en) => IsEnglish ? en : zh;

    // ==================== 带参数的拼接文案 ====================

    public string LogCount(int count) => IsEnglish ? $"{count} entries" : $"共 {count} 条";

    public string LogsCopied(int count) => IsEnglish ? $"Copied {count} entries" : $"已复制 {count} 条日志";

    public string LogsCopyEmpty() => IsEnglish ? "No logs to copy" : "没有可复制的日志";

    public string LogsCleared() => IsEnglish ? "Logs cleared" : "已清空日志";

    public string ConnectionsCount(int count) => IsEnglish ? $"Connections ({count})" : $"活动连接 ({count})";

    public string AllOutbounds() => IsEnglish ? "All outbounds" : "全部出站";

    public string RemainingDays(int days) => IsEnglish ? $"{days} days left" : $"剩余 {days} 天";

    public string ExpiredOn(string date) => IsEnglish ? $"{date} (expired)" : $"{date}（已过期）";

    public string ExpiresOn(string date, int days) => IsEnglish
        ? $"{date} ({days} days left)"
        : $"{date}（剩余 {days} 天）";

    public string ExpireUnknown() => IsEnglish ? "Lifetime / Unknown" : "长期有效 / 未知";

    public string UsedAmount(string amount) => IsEnglish ? $"Used {amount}" : $"已用 {amount}";

    public string RemainingAmount(string amount) => IsEnglish ? $"{amount} left" : $"剩余 {amount}";

    public string NoProfileSelected() => IsEnglish ? "No profile selected" : "未选择订阅";

    public string NotSelected() => IsEnglish ? "(none)" : "（未选择）";

    public string DeleteProfileConfirm(string name) => IsEnglish
        ? $"Delete \"{name}\"? Its config file will be deleted too."
        : $"确定删除「{name}」吗？该操作会同时删除其配置文件。";

    public string ProfileSummary(string name, string nodes) => IsEnglish ? $"Profile: {name} ({nodes})" : $"订阅：{name}（{nodes}）";

    public string ConfigSummary(string file) => IsEnglish ? $"Config: {file}" : $"配置：{file}";

    public string NodesCountText(int count) => IsEnglish ? $"{count} nodes" : $"{count} 个节点";

    /// <summary>订阅类型：在线订阅 / 本地配置。</summary>
    public string ProfileTypeText(bool isRemote) => isRemote
        ? (IsEnglish ? "Remote" : "在线订阅")
        : (IsEnglish ? "Local" : "本地配置");

    /// <summary>定时更新间隔。</summary>
    public string ProfileIntervalText(int minutes) => minutes switch
    {
        0 => CommonNever,
        60 => CommonHour1,
        360 => CommonHour6,
        720 => CommonHour12,
        1440 => CommonHour24,
        _ => IsEnglish ? $"Every {minutes} min" : $"每 {minutes} 分钟",
    };

    /// <summary>订阅最后更新时间。</summary>
    public string ProfileUpdatedText(DateTimeOffset? last)
    {
        if (last is not { } time)
        {
            return IsEnglish ? "Never updated" : "尚未更新";
        }

        var text = time.LocalDateTime.ToString(
            "yyyy-MM-dd HH:mm",
            System.Globalization.CultureInfo.CurrentCulture);

        return IsEnglish ? $"Updated {text}" : $"最后更新 {text}";
    }

    // ==================== 规则集状态 ====================
    /// <summary>
    /// 规则集条目状态文案。入参是 RuleSetStatus 的稳定键（可能带 fail: 前缀），
    /// 状态本身不进数据库、不参与比较，只有显示时才翻译，避免切语言影响更新逻辑。
    /// </summary>
    public string RuleSetStatusText(string status)
    {
        if (status.StartsWith(RuleSetStatus.FailedPrefix, StringComparison.Ordinal))
        {
            var reason = status[RuleSetStatus.FailedPrefix.Length..];
            return IsEnglish ? $"Failed: {reason}" : $"失败：{reason}";
        }

        return status switch
        {
            RuleSetStatus.Ready => IsEnglish ? "Ready" : "就绪",
            RuleSetStatus.Updating => IsEnglish ? "Updating..." : "更新中…",
            RuleSetStatus.Updated => IsEnglish ? "Updated" : "已更新",
            RuleSetStatus.Skipped => IsEnglish ? "Invalid, skipped" : "内容无效，已跳过",
            RuleSetStatus.Missing => IsEnglish ? "File missing" : "文件缺失",
            RuleSetStatus.Custom => IsEnglish ? "Custom file, skipped" : "自定义文件，跳过",
            RuleSetStatus.PendingRestart => IsEnglish ? "Pending (restart core)" : "待更新（重启内核后生效）",
            _ => status,
        };
    }

    /// <summary>规则集卡片摘要：总数 + 上次更新时间 + 待生效数量。</summary>
    public string RuleSetSummary(int count, DateTime? last, int pending)
    {
        var when = last is null
            ? (IsEnglish ? "never" : "从未")
            : last.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

        var text = IsEnglish
            ? $"{count} local rule-sets · Last update: {when}"
            : $"共 {count} 个本地规则集 · 上次更新：{when}";

        if (pending <= 0)
        {
            return text;
        }

        return IsEnglish
            ? $"{text} · {pending} pending core restart"
            : $"{text} · {pending} 个待内核重启后生效";
    }

    public string RuleSetPreparing() => IsEnglish ? "Preparing..." : "准备中…";

    public string RuleSetUpdating(int done, int total) => IsEnglish
        ? $"Updating {done}/{total}..."
        : $"正在更新 {done}/{total}…";

    public string RuleSetDone(string detail) => IsEnglish ? $"Done: {detail}" : $"完成：{detail}";

    public string RuleSetFailed(string message) => IsEnglish
        ? $"Update failed: {message}"
        : $"更新失败：{message}";

    /// <summary>一次批量更新的结果统计。</summary>
    public string RuleSetResult(int updated, int pending, int skipped, int failed)
    {
        if (IsEnglish)
        {
            var tail = pending > 0
                ? $"{updated} updated, {pending} pending core restart"
                : $"{updated} updated";
            return $"{tail} ({skipped} skipped, {failed} failed)";
        }
        else
        {
            var tail = pending > 0
                ? $"已更新 {updated} 个，{pending} 个待内核重启后生效"
                : $"已更新 {updated} 个";
            return $"{tail}（跳过 {skipped}，失败 {failed}）";
        }
    }

    // ==================== 连接性检测 / 内核更新 动态文案 ====================

    /// <summary>延迟胶囊文字；ms &lt; 0 表示失败或超时。</summary>
    public string ConnLatencyText(long ms) => ms < 0
        ? (IsEnglish ? "Failed" : "失败")
        : $"{ms} ms";

    public string ConnRegionText(string country, string isp)
    {
        if (string.IsNullOrWhiteSpace(country) && string.IsNullOrWhiteSpace(isp))
        {
            return T(nameof(ConnUnknownRegion));
        }

        if (string.IsNullOrWhiteSpace(country)) return isp;
        if (string.IsNullOrWhiteSpace(isp)) return country;
        return $"{country} · {isp}";
    }

    public string KernelVersionDisplay(string version) => string.IsNullOrWhiteSpace(version)
        ? T(nameof(KernelNotInstalled))
        : $"v{version}";

    public string KernelNewVersionText(string version) => IsEnglish
        ? $"New version v{version} available"
        : $"发现新版本 v{version}";

    public string KernelUpdatedText(string version) => IsEnglish
        ? $"Core updated to v{version}"
        : $"内核已更新至 v{version}";

    public string KernelStageText(string stage, int percent) => $"{stage} {percent}%";

    public string KernelAssetSizeText(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";

    public string KernelCheckFailedText(string message) => IsEnglish
        ? $"Update check failed: {message}"
        : $"检查更新失败：{message}";

    public string KernelUpdateFailedText(string message) => IsEnglish
        ? $"Update failed: {message}"
        : $"更新失败：{message}";

    // ==================== 文件选择器 / 导入 / Mixin ====================

    /// <summary>按扩展名生成文件类型过滤器的显示名，如 ".json" → "JSON files (*.json)"。</summary>
    public string PickerFilterLabel(string ext)
    {
        var tag = ext.TrimStart('.').ToUpperInvariant();
        return IsEnglish ? $"{tag} files (*{ext})" : $"{tag} 文件 (*{ext})";
    }

    public string PickerFailedMessage(string reason) => IsEnglish
        ? $"Could not open the file dialog ({reason})."
        : $"无法打开文件对话框（{reason}）。";

    /// <summary>概览页大号状态标题：按「运行中 + 当前开启项」组合。</summary>
    public string StatusTitle(bool running, bool proxy, bool tun)
    {
        if (!running)
        {
            return T(nameof(StatusStopped));
        }

        if (proxy && tun)
        {
            return T(nameof(StatusRunningBoth));
        }

        if (proxy)
        {
            return T(nameof(StatusRunningProxy));
        }

        if (tun)
        {
            return T(nameof(StatusRunningTun));
        }

        return T(nameof(StatusRunning));
    }

    // ==================== 关于 / 客户端更新 ====================

    public string AppNewVersionText(string version) => IsEnglish
        ? $"New version {version} available"
        : $"发现新版本 {version}";

    public string AppCheckFailedText(string reason) => IsEnglish
        ? $"Update check failed: {reason}"
        : $"检查更新失败：{reason}";

    public string AppUpdateAvailableLog(string version) => IsEnglish
        ? $"[Notice] New client version {version} is available. See Settings > About."
        : $"[提示] 发现客户端新版本 {version}，可在设置页「关于」中查看。";

    // ==================== 强类型词条 ====================

    /// <summary>概览</summary>
    public string NavOverview => T(nameof(NavOverview));

    /// <summary>节点策略</summary>
    public string NavProxies => T(nameof(NavProxies));

    /// <summary>活动连接</summary>
    public string NavConnections => T(nameof(NavConnections));

    /// <summary>运行日志</summary>
    public string NavLogs => T(nameof(NavLogs));

    /// <summary>订阅管理</summary>
    public string NavProfiles => T(nameof(NavProfiles));

    /// <summary>设置</summary>
    public string NavSettings => T(nameof(NavSettings));

    /// <summary>内存</summary>
    public string SidebarMemory => T(nameof(SidebarMemory));

    /// <summary>显示主界面</summary>
    public string TrayShowWindow => T(nameof(TrayShowWindow));

    /// <summary>启动内核</summary>
    public string TrayStartCore => T(nameof(TrayStartCore));

    /// <summary>停止内核</summary>
    public string TrayStopCore => T(nameof(TrayStopCore));

    /// <summary>彻底退出</summary>
    public string TrayExit => T(nameof(TrayExit));

    /// <summary>规则 (Rule)</summary>
    public string TrayModeRule => T(nameof(TrayModeRule));

    /// <summary>全局 (Global)</summary>
    public string TrayModeGlobal => T(nameof(TrayModeGlobal));

    /// <summary>直连 (Direct)</summary>
    public string TrayModeDirect => T(nameof(TrayModeDirect));

    /// <summary>确定</summary>
    public string CommonOk => T(nameof(CommonOk));

    /// <summary>取消</summary>
    public string CommonCancel => T(nameof(CommonCancel));

    /// <summary>保存</summary>
    public string CommonSave => T(nameof(CommonSave));

    /// <summary>删除</summary>
    public string CommonDelete => T(nameof(CommonDelete));

    /// <summary>恢复默认</summary>
    public string CommonRestoreDefault => T(nameof(CommonRestoreDefault));

    /// <summary>1 分钟</summary>
    public string CommonMinutes1 => T(nameof(CommonMinutes1));

    /// <summary>3 分钟</summary>
    public string CommonMinutes3 => T(nameof(CommonMinutes3));

    /// <summary>5 分钟</summary>
    public string CommonMinutes5 => T(nameof(CommonMinutes5));

    /// <summary>10 分钟</summary>
    public string CommonMinutes10 => T(nameof(CommonMinutes10));

    /// <summary>15 分钟</summary>
    public string CommonMinutes15 => T(nameof(CommonMinutes15));

    /// <summary>30 分钟</summary>
    public string CommonMinutes30 => T(nameof(CommonMinutes30));

    /// <summary>60 分钟</summary>
    public string CommonMinutes60 => T(nameof(CommonMinutes60));

    /// <summary>每 1 小时</summary>
    public string CommonHour1 => T(nameof(CommonHour1));

    /// <summary>每 6 小时</summary>
    public string CommonHour6 => T(nameof(CommonHour6));

    /// <summary>每 12 小时</summary>
    public string CommonHour12 => T(nameof(CommonHour12));

    /// <summary>每 24 小时</summary>
    public string CommonHour24 => T(nameof(CommonHour24));

    /// <summary>不自动更新</summary>
    public string CommonNever => T(nameof(CommonNever));

    /// <summary>运行中</summary>
    public string StatusRunning => T(nameof(StatusRunning));

    /// <summary>未运行</summary>
    public string StatusStopped => T(nameof(StatusStopped));

    /// <summary>切换/管理订阅</summary>
    public string HomeManageProfiles => T(nameof(HomeManageProfiles));

    /// <summary>启停 sing-box 内核</summary>
    public string HomeToggleCore => T(nameof(HomeToggleCore));

    /// <summary>当前订阅</summary>
    public string HomeCurrentProfile => T(nameof(HomeCurrentProfile));

    /// <summary>更新订阅</summary>
    public string HomeUpdateProfile => T(nameof(HomeUpdateProfile));

    /// <summary>更新时间</summary>
    public string HomeUpdatedAt => T(nameof(HomeUpdatedAt));

    /// <summary>到期时间</summary>
    public string HomeExpiresAt => T(nameof(HomeExpiresAt));

    /// <summary>已用 / 总流量</summary>
    public string HomeUsedTotal => T(nameof(HomeUsedTotal));

    /// <summary>订阅流量使用比例</summary>
    public string HomeUsageA11y => T(nameof(HomeUsageA11y));

    /// <summary>在线订阅</summary>
    public string HomeRemoteProfile => T(nameof(HomeRemoteProfile));

    /// <summary>本地配置</summary>
    public string HomeLocalProfile => T(nameof(HomeLocalProfile));

    /// <summary>TUN 需要管理员权限，否则内核无法创建网卡。</summary>
    public string HomeTunAdminHint => T(nameof(HomeTunAdminHint));

    /// <summary>以管理员身份重启（TUN 必需）</summary>
    public string HomeRestartAsAdmin => T(nameof(HomeRestartAsAdmin));

    /// <summary>关闭提示</summary>
    public string HomeDismiss => T(nameof(HomeDismiss));

    /// <summary>应用图标</summary>
    public string HomeAppIcon => T(nameof(HomeAppIcon));

    /// <summary>代理模式</summary>
    public string HomeProxyMode => T(nameof(HomeProxyMode));

    /// <summary>运行中切换立即生效</summary>
    public string HomeProxyModeHint => T(nameof(HomeProxyModeHint));

    /// <summary>规则</summary>
    public string HomeModeRule => T(nameof(HomeModeRule));

    /// <summary>全局</summary>
    public string HomeModeGlobal => T(nameof(HomeModeGlobal));

    /// <summary>直连</summary>
    public string HomeModeDirect => T(nameof(HomeModeDirect));

    /// <summary>规则模式</summary>
    public string HomeModeRuleA11y => T(nameof(HomeModeRuleA11y));

    /// <summary>全局模式</summary>
    public string HomeModeGlobalA11y => T(nameof(HomeModeGlobalA11y));

    /// <summary>直连模式</summary>
    public string HomeModeDirectA11y => T(nameof(HomeModeDirectA11y));

    /// <summary>网络设置</summary>
    public string HomeNetworkSettings => T(nameof(HomeNetworkSettings));

    /// <summary>系统代理</summary>
    public string HomeSystemProxy => T(nameof(HomeSystemProxy));

    /// <summary>启用系统代理</summary>
    public string HomeSystemProxyA11y => T(nameof(HomeSystemProxyA11y));

    /// <summary>TUN 模式</summary>
    public string HomeTunMode => T(nameof(HomeTunMode));

    /// <summary>启用 TUN 模式</summary>
    public string HomeTunModeA11y => T(nameof(HomeTunModeA11y));

    /// <summary>刷新节点</summary>
    public string ProxiesRefresh => T(nameof(ProxiesRefresh));

    /// <summary>一键测速</summary>
    public string ProxiesTestLatency => T(nameof(ProxiesTestLatency));

    /// <summary>测速中</summary>
    public string ProxiesTesting => T(nameof(ProxiesTesting));

    /// <summary>排序: 默认</summary>
    public string ProxiesSortDefault => T(nameof(ProxiesSortDefault));

    /// <summary>排序: 延迟</summary>
    public string ProxiesSortDelay => T(nameof(ProxiesSortDelay));

    /// <summary>排序: 名称</summary>
    public string ProxiesSortName => T(nameof(ProxiesSortName));

    /// <summary>切换升序 / 降序</summary>
    public string ProxiesToggleSort => T(nameof(ProxiesToggleSort));

    /// <summary>切换升降序</summary>
    public string ProxiesToggleSortA11y => T(nameof(ProxiesToggleSortA11y));

    /// <summary>点击卡片切换节点</summary>
    public string ProxiesClickCardHint => T(nameof(ProxiesClickCardHint));

    /// <summary>请先在「概览与日志」中启动内核</summary>
    public string ProxiesStartCoreFirst => T(nameof(ProxiesStartCoreFirst));

    /// <summary>订阅名称</summary>
    public string ProfilesNewName => T(nameof(ProfilesNewName));

    /// <summary>新订阅名称</summary>
    public string ProfilesNewNameA11y => T(nameof(ProfilesNewNameA11y));

    /// <summary>订阅链接（https://...）</summary>
    public string ProfilesNewUrl => T(nameof(ProfilesNewUrl));

    /// <summary>新订阅链接</summary>
    public string ProfilesNewUrlA11y => T(nameof(ProfilesNewUrlA11y));

    /// <summary>导入在线订阅</summary>
    public string ProfilesImportRemote => T(nameof(ProfilesImportRemote));

    /// <summary>导入本地文件</summary>
    public string ProfilesImportLocal => T(nameof(ProfilesImportLocal));

    /// <summary>更新全部订阅</summary>
    public string ProfilesUpdateAll => T(nameof(ProfilesUpdateAll));

    /// <summary>立即更新</summary>
    public string ProfilesUpdateNow => T(nameof(ProfilesUpdateNow));

    /// <summary>启用此订阅</summary>
    public string ProfilesEnable => T(nameof(ProfilesEnable));

    /// <summary>当前使用</summary>
    public string ProfilesInUse => T(nameof(ProfilesInUse));

    /// <summary>重命名/编辑</summary>
    public string ProfilesRenameEdit => T(nameof(ProfilesRenameEdit));

    /// <summary>定时更新</summary>
    public string ProfilesInterval => T(nameof(ProfilesInterval));

    /// <summary>定时更新周期</summary>
    public string ProfilesIntervalA11y => T(nameof(ProfilesIntervalA11y));

    /// <summary>全局拓展配置（Mixin）</summary>
    public string ProfilesMixinTitle => T(nameof(ProfilesMixinTitle));

    /// <summary>全局前置拓展</summary>
    public string ProfilesPreMixin => T(nameof(ProfilesPreMixin));

    /// <summary>全局后置拓展</summary>
    public string ProfilesPostMixin => T(nameof(ProfilesPostMixin));

    /// <summary>覆盖订阅默认字段，并将规则/节点前置插入（高优先级抢占）</summary>
    public string ProfilesPreMixinHint => T(nameof(ProfilesPreMixinHint));

    /// <summary>在订阅配置末尾追加自定义规则、规则集或自建节点</summary>
    public string ProfilesPostMixinHint => T(nameof(ProfilesPostMixinHint));

    /// <summary>编辑前置 JSON</summary>
    public string ProfilesEditPreJson => T(nameof(ProfilesEditPreJson));

    /// <summary>编辑后置 JSON</summary>
    public string ProfilesEditPostJson => T(nameof(ProfilesEditPostJson));

    /// <summary>启用全局前置拓展</summary>
    public string ProfilesEnablePreA11y => T(nameof(ProfilesEnablePreA11y));

    /// <summary>启用全局后置拓展</summary>
    public string ProfilesEnablePostA11y => T(nameof(ProfilesEnablePostA11y));

    /// <summary>编辑订阅</summary>
    public string ProfilesEditTitle => T(nameof(ProfilesEditTitle));

    /// <summary>删除订阅</summary>
    public string ProfilesDeleteTitle => T(nameof(ProfilesDeleteTitle));

    /// <summary>前置拓展 JSON</summary>
    public string ProfilesPreMixinJson => T(nameof(ProfilesPreMixinJson));

    /// <summary>后置拓展 JSON</summary>
    public string ProfilesPostMixinJson => T(nameof(ProfilesPostMixinJson));

    /// <summary>用系统默认编辑器打开文件</summary>
    public string ProfilesOpenWithEditor => T(nameof(ProfilesOpenWithEditor));

    /// <summary>编辑全局前置拓展</summary>
    public string ProfilesEditPreMixinTitle => T(nameof(ProfilesEditPreMixinTitle));

    /// <summary>编辑全局后置拓展</summary>
    public string ProfilesEditPostMixinTitle => T(nameof(ProfilesEditPostMixinTitle));

    /// <summary>本地配置不可修改链接</summary>
    public string ProfilesLocalUrlReadonly => T(nameof(ProfilesLocalUrlReadonly));

    /// <summary>暂停刷新</summary>
    public string ConnPause => T(nameof(ConnPause));

    /// <summary>继续刷新</summary>
    public string ConnResume => T(nameof(ConnResume));

    /// <summary>显示参数</summary>
    public string ConnShowColumns => T(nameof(ConnShowColumns));

    /// <summary>全部断开</summary>
    public string ConnCloseAll => T(nameof(ConnCloseAll));

    /// <summary>排序维度</summary>
    public string ConnSortByA11y => T(nameof(ConnSortByA11y));

    /// <summary>连接搜索</summary>
    public string ConnSearchA11y => T(nameof(ConnSearchA11y));

    /// <summary>搜索 域名 / IP / 进程 / 规则 / 出站</summary>
    public string ConnSearchPlaceholder => T(nameof(ConnSearchPlaceholder));

    /// <summary>目标域名</summary>
    public string ConnColHost => T(nameof(ConnColHost));

    /// <summary>目标 IP 地址</summary>
    public string ConnColDestinationIp => T(nameof(ConnColDestinationIp));

    /// <summary>进程名称</summary>
    public string ConnColProcess => T(nameof(ConnColProcess));

    /// <summary>命中路由规则</summary>
    public string ConnColRule => T(nameof(ConnColRule));

    /// <summary>出站节点链路</summary>
    public string ConnColChain => T(nameof(ConnColChain));

    /// <summary>出站筛选</summary>
    public string ConnOutboundFilterA11y => T(nameof(ConnOutboundFilterA11y));

    /// <summary>入站与协议</summary>
    public string ConnColInbound => T(nameof(ConnColInbound));

    /// <summary>出站策略组</summary>
    public string ConnColOutboundGroup => T(nameof(ConnColOutboundGroup));

    /// <summary>建立时间</summary>
    public string ConnColStart => T(nameof(ConnColStart));

    /// <summary>连接持续时长</summary>
    public string ConnColDuration => T(nameof(ConnColDuration));

    /// <summary>上传速度</summary>
    public string ConnColUploadRate => T(nameof(ConnColUploadRate));

    /// <summary>下载速度</summary>
    public string ConnColDownloadRate => T(nameof(ConnColDownloadRate));

    /// <summary>上传总流量</summary>
    public string ConnColUploadTotal => T(nameof(ConnColUploadTotal));

    /// <summary>下载总流量</summary>
    public string ConnColDownloadTotal => T(nameof(ConnColDownloadTotal));

    /// <summary>累计上下行流量</summary>
    public string ConnColTrafficTotal => T(nameof(ConnColTrafficTotal));

    /// <summary>实时上下行速度</summary>
    public string ConnColSpeedTotal => T(nameof(ConnColSpeedTotal));

    /// <summary>关闭连接</summary>
    public string ConnCloseA11y => T(nameof(ConnCloseA11y));

    /// <summary>没有匹配的连接</summary>
    public string ConnEmptyFiltered => T(nameof(ConnEmptyFiltered));

    /// <summary>暂无活动连接</summary>
    public string ConnEmpty => T(nameof(ConnEmpty));

    /// <summary>搜索日志内容、规则或节点...</summary>
    public string LogsSearchPlaceholder => T(nameof(LogsSearchPlaceholder));

    /// <summary>日志搜索</summary>
    public string LogsSearchA11y => T(nameof(LogsSearchA11y));

    /// <summary>日志级别筛选</summary>
    public string LogsLevelA11y => T(nameof(LogsLevelA11y));

    /// <summary>全部级别</summary>
    public string LogsLevelAll => T(nameof(LogsLevelAll));

    /// <summary>仅 Info 及以上</summary>
    public string LogsLevelInfoUp => T(nameof(LogsLevelInfoUp));

    /// <summary>仅 Warning 及以上</summary>
    public string LogsLevelWarnUp => T(nameof(LogsLevelWarnUp));

    /// <summary>仅 Error</summary>
    public string LogsLevelError => T(nameof(LogsLevelError));

    /// <summary>自动滚动</summary>
    public string LogsAutoScroll => T(nameof(LogsAutoScroll));

    /// <summary>复制全部日志</summary>
    public string LogsCopyAllA11y => T(nameof(LogsCopyAllA11y));

    /// <summary>复制</summary>
    public string LogsCopy => T(nameof(LogsCopy));

    /// <summary>导出日志</summary>
    public string LogsExportA11y => T(nameof(LogsExportA11y));

    /// <summary>导出文件</summary>
    public string LogsExport => T(nameof(LogsExport));

    /// <summary>清空日志</summary>
    public string LogsClearA11y => T(nameof(LogsClearA11y));

    /// <summary>清空</summary>
    public string LogsClear => T(nameof(LogsClear));

    /// <summary>暂无日志</summary>
    public string LogsEmpty => T(nameof(LogsEmpty));

    /// <summary>启动与后台</summary>
    public string SetSectionStartup => T(nameof(SetSectionStartup));

    /// <summary>通用与网络</summary>
    public string SetSectionGeneral => T(nameof(SetSectionGeneral));

    /// <summary>TUN 虚拟网卡设置</summary>
    public string SetSectionTun => T(nameof(SetSectionTun));

    /// <summary>备份与恢复</summary>
    public string SetSectionBackup => T(nameof(SetSectionBackup));

    /// <summary>高级与目录</summary>
    public string SetSectionAdvanced => T(nameof(SetSectionAdvanced));

    /// <summary>语言 / Language</summary>
    public string SetLanguageLabel => T(nameof(SetLanguageLabel));

    /// <summary>切换后立即生效，无需重启</summary>
    public string SetLanguageHint => T(nameof(SetLanguageHint));

    /// <summary>简体中文</summary>
    public string SetLanguageChinese => T(nameof(SetLanguageChinese));

    /// <summary>English</summary>
    public string SetLanguageEnglish => T(nameof(SetLanguageEnglish));

    /// <summary>外观主题</summary>
    public string SetAppearance => T(nameof(SetAppearance));

    /// <summary>跟随系统</summary>
    public string SetThemeSystem => T(nameof(SetThemeSystem));

    /// <summary>浅色</summary>
    public string SetThemeLight => T(nameof(SetThemeLight));

    /// <summary>深色</summary>
    public string SetThemeDark => T(nameof(SetThemeDark));

    /// <summary>开机自动启动</summary>
    public string SetAutoStartApp => T(nameof(SetAutoStartApp));

    /// <summary>登录 Windows 后自动启动客户端</summary>
    public string SetAutoStartAppHint => T(nameof(SetAutoStartAppHint));

    /// <summary>自启时静默驻留托盘</summary>
    public string SetSilentTray => T(nameof(SetSilentTray));

    /// <summary>开机启动时不显示主窗口，直接缩到托盘</summary>
    public string SetSilentTrayHint => T(nameof(SetSilentTrayHint));

    /// <summary>打开软件时自动启动内核</summary>
    public string SetAutoStartCore => T(nameof(SetAutoStartCore));

    /// <summary>客户端启动后自动拉起 sing-box</summary>
    public string SetAutoStartCoreHint => T(nameof(SetAutoStartCoreHint));

    /// <summary>关闭主窗口时隐藏到系统托盘</summary>
    public string SetCloseToTray => T(nameof(SetCloseToTray));

    /// <summary>关闭时保持后台运行；从托盘菜单「彻底退出」才会真正结束</summary>
    public string SetCloseToTrayHint => T(nameof(SetCloseToTrayHint));

    /// <summary>后台轻量模式</summary>
    public string SetLightweight => T(nameof(SetLightweight));

    /// <summary>隐藏到系统托盘达到指定时间后，自动暂停 UI 遥测轮询、释放非活动页面缓存并压缩进程内存工作集</summary>
    public string SetLightweightHint => T(nameof(SetLightweightHint));

    /// <summary>轻量模式触发时间</summary>
    public string SetLightweightDelayA11y => T(nameof(SetLightweightDelayA11y));

    /// <summary>当前非管理员，仅创建普通权限自启；若需开机自动开启 TUN，请先提权后再开启此开关。</summary>
    public string SetAutoStartAppAdminHint => T(nameof(SetAutoStartAppAdminHint));

    /// <summary>启用 IPv6 支持</summary>
    public string SetIpv6 => T(nameof(SetIpv6));

    /// <summary>控制 DNS 解析策略与 TUN 虚拟网卡的 IPv6 双栈支持（修改后若内核正在运行将自动重启生效）</summary>
    public string SetIpv6Hint => T(nameof(SetIpv6Hint));

    /// <summary>统一延迟测试</summary>
    public string SetUnifiedDelay => T(nameof(SetUnifiedDelay));

    /// <summary>开启后将连续进行两次延迟测试并取第二次结果，消除不同协议首次连接握手带来的延迟偏差</summary>
    public string SetUnifiedDelayHint => T(nameof(SetUnifiedDelayHint));

    /// <summary>节点测速地址</summary>
    public string SetSpeedTestUrl => T(nameof(SetSpeedTestUrl));

    /// <summary>用于策略组「一键测速」的探测地址，需返回 2xx</summary>
    public string SetSpeedTestUrlHint => T(nameof(SetSpeedTestUrlHint));

    /// <summary>后台自动检测延迟</summary>
    public string SetAutoTestDelay => T(nameof(SetAutoTestDelay));

    /// <summary>内核运行期间按固定周期静默重测策略组延迟，并按当前排序规则刷新顺序</summary>
    public string SetAutoTestDelayHint => T(nameof(SetAutoTestDelayHint));

    /// <summary>自动测速间隔</summary>
    public string SetAutoTestIntervalA11y => T(nameof(SetAutoTestIntervalA11y));

    /// <summary>默认 9000，链路异常时可尝试降到 1500</summary>
    public string SetTunMtuHint => T(nameof(SetTunMtuHint));

    /// <summary>网卡名称</summary>
    public string SetTunInterface => T(nameof(SetTunInterface));

    /// <summary>写入 interface_name，留空则由内核自动命名</summary>
    public string SetTunInterfaceHint => T(nameof(SetTunInterfaceHint));

    /// <summary>TUN 网卡名称</summary>
    public string SetTunInterfaceA11y => T(nameof(SetTunInterfaceA11y));

    /// <summary>网络栈</summary>
    public string SetTunStack => T(nameof(SetTunStack));

    /// <summary>system 走系统协议栈，gvisor 兼容性更好，mixed 由内核自动选择</summary>
    public string SetTunStackHint => T(nameof(SetTunStackHint));

    /// <summary>TUN 网络栈</summary>
    public string SetTunStackA11y => T(nameof(SetTunStackA11y));

    /// <summary>自动配置路由</summary>
    public string SetTunAutoRoute => T(nameof(SetTunAutoRoute));

    /// <summary>auto_route，接管系统路由表</summary>
    public string SetTunAutoRouteHint => T(nameof(SetTunAutoRouteHint));

    /// <summary>TUN 自动配置路由</summary>
    public string SetTunAutoRouteA11y => T(nameof(SetTunAutoRouteA11y));

    /// <summary>严格路由</summary>
    public string SetTunStrictRoute => T(nameof(SetTunStrictRoute));

    /// <summary>strict_route，防止流量绕过 TUN 造成泄漏</summary>
    public string SetTunStrictRouteHint => T(nameof(SetTunStrictRouteHint));

    /// <summary>TUN 严格路由</summary>
    public string SetTunStrictRouteA11y => T(nameof(SetTunStrictRouteA11y));

    /// <summary>应用并重启 TUN</summary>
    public string SetTunApply => T(nameof(SetTunApply));

    /// <summary>内核正以 TUN 运行时，点右侧按钮会按新参数热重启。</summary>
    public string SetTunApplyHint => T(nameof(SetTunApplyHint));

    /// <summary>修改后立即生效</summary>
    public string SetAppliesImmediately => T(nameof(SetAppliesImmediately));

    /// <summary>sing-box 内核版本</summary>
    public string SetCoreVersion => T(nameof(SetCoreVersion));

    /// <summary>配置与内核目录</summary>
    public string SetCoreDirectory => T(nameof(SetCoreDirectory));

    /// <summary>config.json、sing-box.exe 与运行配置都在这里</summary>
    public string SetCoreDirectoryHint => T(nameof(SetCoreDirectoryHint));

    /// <summary>打开目录</summary>
    public string SetOpenDirectory => T(nameof(SetOpenDirectory));

    /// <summary>规则集（Rule-sets）管理与更新</summary>
    public string SetRuleSets => T(nameof(SetRuleSets));

    /// <summary>自动更新</summary>
    public string SetRuleSetsAutoUpdate => T(nameof(SetRuleSetsAutoUpdate));

    /// <summary>自动更新规则集</summary>
    public string SetRuleSetsAutoUpdateA11y => T(nameof(SetRuleSetsAutoUpdateA11y));

    /// <summary>立即更新全部</summary>
    public string SetRuleSetsUpdateAll => T(nameof(SetRuleSetsUpdateAll));

    /// <summary>立即更新全部规则集</summary>
    public string SetRuleSetsUpdateAllA11y => T(nameof(SetRuleSetsUpdateAllA11y));

    /// <summary>规则集下载地址</summary>
    public string SetRuleSetsUrl => T(nameof(SetRuleSetsUrl));

    /// <summary>规则集下载地址模板，支持 {category} {short} {name} {file}</summary>
    public string SetRuleSetsUrlPlaceholder => T(nameof(SetRuleSetsUrlPlaceholder));

    /// <summary>恢复默认规则集地址</summary>
    public string SetRuleSetsUrlReset => T(nameof(SetRuleSetsUrlReset));

    /// <summary>查看规则集文件列表</summary>
    public string SetRuleSetsList => T(nameof(SetRuleSetsList));

    /// <summary>备份包含客户端设置、全部订阅配置与拓展配置（不含内核程序与缓存）</summary>
    public string SetBackupHint => T(nameof(SetBackupHint));

    /// <summary>导出本地备份</summary>
    public string SetExportBackup => T(nameof(SetExportBackup));

    /// <summary>从本地备份恢复</summary>
    public string SetRestoreLocal => T(nameof(SetRestoreLocal));

    /// <summary>启动时自动 WebDAV 备份</summary>
    public string SetAutoWebDav => T(nameof(SetAutoWebDav));

    /// <summary>每次启动后在后台静默上传一次，失败只记日志不打扰</summary>
    public string SetAutoWebDavHint => T(nameof(SetAutoWebDavHint));

    /// <summary>WebDAV 地址</summary>
    public string SetWebDavUrl => T(nameof(SetWebDavUrl));

    /// <summary>WebDAV 地址（https://dav.jianguoyun.com/dav/SingBoxBackup/）</summary>
    public string SetWebDavUrlPlaceholder => T(nameof(SetWebDavUrlPlaceholder));

    /// <summary>WebDAV 用户名</summary>
    public string SetWebDavUser => T(nameof(SetWebDavUser));

    /// <summary>WebDAV 密码</summary>
    public string SetWebDavPassword => T(nameof(SetWebDavPassword));

    /// <summary>测试连接</summary>
    public string SetTestConnection => T(nameof(SetTestConnection));

    /// <summary>测试 WebDAV 连接</summary>
    public string SetTestConnectionA11y => T(nameof(SetTestConnectionA11y));

    /// <summary>立即 WebDAV 备份</summary>
    public string SetWebDavBackupNow => T(nameof(SetWebDavBackupNow));

    /// <summary>从 WebDAV 恢复</summary>
    public string SetRestoreWebDav => T(nameof(SetRestoreWebDav));

    /// <summary>恢复备份将覆盖当前的客户端设置、所有订阅及拓展配置，是否继续？</summary>
    public string SetRestoreConfirm => T(nameof(SetRestoreConfirm));

    /// <summary>确认恢复</summary>
    public string SetRestoreConfirmButton => T(nameof(SetRestoreConfirmButton));

    /// <summary>备份包</summary>
    public string SetBackupFileType => T(nameof(SetBackupFileType));

    /// <summary>网络连通性与出口信息</summary>
    public string ConnCardTitle => T(nameof(ConnCardTitle));

    /// <summary>出口 IP</summary>
    public string ConnExitIp => T(nameof(ConnExitIp));

    /// <summary>地区 / 运营商</summary>
    public string ConnRegionIsp => T(nameof(ConnRegionIsp));

    /// <summary>检测中…</summary>
    public string ConnChecking => T(nameof(ConnChecking));

    /// <summary>立即检测</summary>
    public string ConnRecheck => T(nameof(ConnRecheck));

    /// <summary>显示 IP</summary>
    public string ConnShowIp => T(nameof(ConnShowIp));

    /// <summary>隐藏 IP</summary>
    public string ConnHideIp => T(nameof(ConnHideIp));

    /// <summary>复制 IP</summary>
    public string ConnCopyIp => T(nameof(ConnCopyIp));

    /// <summary>未连接</summary>
    public string ConnNotConnected => T(nameof(ConnNotConnected));

    /// <summary>内核未启动</summary>
    public string ConnCoreStopped => T(nameof(ConnCoreStopped));

    /// <summary>检测失败</summary>
    public string ConnFailed => T(nameof(ConnFailed));

    /// <summary>超时</summary>
    public string ConnTimeout => T(nameof(ConnTimeout));

    /// <summary>经本地代理通道</summary>
    public string ConnViaProxy => T(nameof(ConnViaProxy));

    /// <summary>直连探测</summary>
    public string ConnViaDirect => T(nameof(ConnViaDirect));

    /// <summary>已复制出口 IP</summary>
    public string ConnCopied => T(nameof(ConnCopied));

    /// <summary>未知地区</summary>
    public string ConnUnknownRegion => T(nameof(ConnUnknownRegion));

    /// <summary>--</summary>
    public string ConnNoIp => T(nameof(ConnNoIp));

    /// <summary>切换 IP 显示</summary>
    public string ConnIpMaskA11y => T(nameof(ConnIpMaskA11y));

    /// <summary>连通性延迟</summary>
    public string ConnLatencyA11y => T(nameof(ConnLatencyA11y));

    /// <summary>Google</summary>
    public string ConnGoogle => T(nameof(ConnGoogle));

    /// <summary>GitHub</summary>
    public string ConnGithub => T(nameof(ConnGithub));

    /// <summary>Cloudflare</summary>
    public string ConnCloudflare => T(nameof(ConnCloudflare));

    /// <summary>sing-box 版本</summary>
    public string SingBoxVersion => T(nameof(SingBoxVersion));

    /// <summary>sing-box 内核版本</summary>
    public string KernelCardTitle => T(nameof(KernelCardTitle));

    /// <summary>当前版本</summary>
    public string KernelCurrentVersion => T(nameof(KernelCurrentVersion));

    /// <summary>检查更新</summary>
    public string KernelCheckUpdate => T(nameof(KernelCheckUpdate));

    /// <summary>立即更新</summary>
    public string KernelUpdateNow => T(nameof(KernelUpdateNow));

    /// <summary>查看更新日志</summary>
    public string KernelChangelog => T(nameof(KernelChangelog));

    /// <summary>已是最新版本</summary>
    public string KernelUpToDate => T(nameof(KernelUpToDate));

    /// <summary>未安装</summary>
    public string KernelNotInstalled => T(nameof(KernelNotInstalled));

    /// <summary>正在检查更新…</summary>
    public string KernelChecking => T(nameof(KernelChecking));

    /// <summary>检查更新失败</summary>
    public string KernelCheckFailed => T(nameof(KernelCheckFailed));

    /// <summary>尚未检查更新</summary>
    public string KernelNotChecked => T(nameof(KernelNotChecked));

    /// <summary>正在下载内核…</summary>
    public string KernelDownloading => T(nameof(KernelDownloading));

    /// <summary>正在替换内核…</summary>
    public string KernelInstalling => T(nameof(KernelInstalling));

    /// <summary>正在重启内核…</summary>
    public string KernelRestarting => T(nameof(KernelRestarting));

    /// <summary>内核已更新</summary>
    public string KernelUpdated => T(nameof(KernelUpdated));

    /// <summary>更新失败</summary>
    public string KernelUpdateFailed => T(nameof(KernelUpdateFailed));

    /// <summary>已回滚到旧内核</summary>
    public string KernelRolledBack => T(nameof(KernelRolledBack));

    /// <summary>未找到当前架构的发行包</summary>
    public string KernelNoAsset => T(nameof(KernelNoAsset));

    /// <summary>后台轻量模式下不自动检查更新</summary>
    public string KernelLightweightBlocked => T(nameof(KernelLightweightBlocked));

    /// <summary>更新会短暂停机换核，内核运行中会自动恢复连接</summary>
    public string KernelUpdateHint => T(nameof(KernelUpdateHint));

    /// <summary>更新内核</summary>
    public string KernelUpdateConfirmTitle => T(nameof(KernelUpdateConfirmTitle));

    /// <summary>将下载并替换 Core\sing-box.exe，是否继续？</summary>
    public string KernelUpdateConfirm => T(nameof(KernelUpdateConfirm));

    /// <summary>下载大小</summary>
    public string KernelAssetSize => T(nameof(KernelAssetSize));

    /// <summary>新内核预校验失败</summary>
    public string KernelValidateFailed => T(nameof(KernelValidateFailed));

    /// <summary>所有文件 (*.*)</summary>
    public string PickerAllFiles => T(nameof(PickerAllFiles));

    /// <summary>选择文件</summary>
    public string PickerOpenTitle => T(nameof(PickerOpenTitle));

    /// <summary>保存文件</summary>
    public string PickerSaveTitle => T(nameof(PickerSaveTitle));

    /// <summary>文件对话框打开失败</summary>
    public string PickerFailedTitle => T(nameof(PickerFailedTitle));

    /// <summary>导入失败</summary>
    public string ProfilesImportFailedTitle => T(nameof(ProfilesImportFailedTitle));

    /// <summary>重新读取文件</summary>
    public string ProfilesMixinReload => T(nameof(ProfilesMixinReload));

    /// <summary>关于</summary>
    public string AboutCardTitle => T(nameof(AboutCardTitle));

    /// <summary>GitHub 主页</summary>
    public string AboutGitHub => T(nameof(AboutGitHub));

    /// <summary>检查更新</summary>
    public string AboutCheckUpdate => T(nameof(AboutCheckUpdate));

    /// <summary>正在检查更新…</summary>
    public string AboutChecking => T(nameof(AboutChecking));

    /// <summary>前往下载</summary>
    public string AboutDownload => T(nameof(AboutDownload));

    /// <summary>自动检查更新</summary>
    public string AboutAutoCheck => T(nameof(AboutAutoCheck));

    /// <summary>启动应用时自动检测客户端新版本</summary>
    public string AboutAutoCheckHint => T(nameof(AboutAutoCheckHint));

    /// <summary>尚未检查更新</summary>
    public string AboutNotChecked => T(nameof(AboutNotChecked));

    /// <summary>已是最新版本</summary>
    public string AboutUpToDate => T(nameof(AboutUpToDate));

    /// <summary>检查更新失败</summary>
    public string AboutCheckFailed => T(nameof(AboutCheckFailed));

    /// <summary>后台轻量模式下不自动检查更新</summary>
    public string AboutLightweightBlocked => T(nameof(AboutLightweightBlocked));

    /// <summary>运行中 (系统代理)</summary>
    public string StatusRunningProxy => T(nameof(StatusRunningProxy));

    /// <summary>运行中 (TUN)</summary>
    public string StatusRunningTun => T(nameof(StatusRunningTun));

    /// <summary>运行中 (系统代理 + TUN)</summary>
    public string StatusRunningBoth => T(nameof(StatusRunningBoth));

    /// <summary>开启任一开关即启动内核，全部关闭即停止内核</summary>
    public string HomeSwitchHint => T(nameof(HomeSwitchHint));
}
