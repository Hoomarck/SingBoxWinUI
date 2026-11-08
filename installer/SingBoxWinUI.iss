; ==========================================================================
; SingBoxWinUI 安装向导
;
; 同一套脚本通过编译宏 IncludeCore 产出两个安装包：
;   直接编辑下面的 #define IncludeCore 1 / 0，或
;   ISCC.exe ...                    -> with-core（内置 Core\sing-box.exe + 基础规则集）
;   ISCC.exe /DNoCore=1 ...         -> no-core （不打包内核，覆盖安装保留用户原有内核）
;
; 注意：ISPP 的 /Dxxx=0 传进来是字符串 token，`#if IncludeCore == 1` 会报
; "Operator not applicable to this operand type"，`#if IncludeCore` 又恒为真，
; 所以命令行侧统一用 /DNoCore=1 这个「存在即生效」的开关来覆盖。
;
; publish.ps1 会同时传入 AppVersion / SourceDir / OutDir。
; 中文界面用的 ChineseSimplified.isl 为社区翻译，随仓库放在 installer\languages\。
; ==========================================================================

#ifndef IncludeCore
  #define IncludeCore 1
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\SingBoxRunner"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

; 命令行 /DNoCore=1 时强制切到精简版；否则沿用上面的 IncludeCore 值。
#ifdef NoCore
  #undef IncludeCore
  #define IncludeCore 0
#endif

#if IncludeCore
  #define Variant "with-core"
#else
  #define Variant "no-core"
#endif

[Setup]
AppId={{EF620D73-0739-48E2-B48D-A3A2B1C7B645}
AppName=SingBoxWinUI
AppVersion={#AppVersion}
AppVerName=SingBoxWinUI {#AppVersion}
AppPublisher=Hoomarck
AppPublisherURL=https://github.com/Hoomarck/SingBoxWinUI
AppSupportURL=https://github.com/Hoomarck/SingBoxWinUI/issues
AppUpdatesURL=https://github.com/Hoomarck/SingBoxWinUI/releases
DefaultDirName={autopf}\SingBoxWinUI
DefaultGroupName=SingBoxWinUI
DisableProgramGroupPage=yes
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; 开机自启写 HKCU Run；管理安装模式下这是预期行为，关掉编译器的交叉告警。
UsedUserAreasWarning=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
SetupIconFile={#SourceDir}\Assets\app.ico
UninstallDisplayIcon={app}\SingBoxWinUI.exe
UninstallDisplayName=SingBoxWinUI
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Hoomarck
VersionInfoDescription=SingBoxWinUI Setup ({#Variant})
VersionInfoTextVersion=SingBoxWinUI {#AppVersion}
OutputDir={#OutDir}
OutputBaseFilename=SingBoxWinUI-Setup-v{#AppVersion}-win-x64-{#Variant}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.AdditionalOptions=Additional options
chinesesimplified.AdditionalOptions=附加选项
english.AutoStartTask=Start SingBoxWinUI automatically on Windows login
chinesesimplified.AutoStartTask=开机自动启动 SingBoxWinUI
english.LaunchNow=Launch SingBoxWinUI now
chinesesimplified.LaunchNow=立即运行 SingBoxWinUI

[Tasks]
; 桌面快捷方式默认勾选（不写 unchecked 即为选中）。
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalOptions}"
; 开机自启由用户主动勾选，默认不选，避免静默改动系统启动项。
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: unchecked

[Dirs]
; Program Files 默认只读：显式放开，保证普通权限启动也能写配置、更新内核与规则集。
Name: "{app}"; Permissions: users-modify
Name: "{app}\Core"; Permissions: users-modify
Name: "{app}\rulesets"; Permissions: users-modify

[Files]
; --- 程序本体（根目录文件 + 资源子目录）---
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\en-us\*"; DestDir: "{app}\en-us"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\zh-CN\*"; DestDir: "{app}\zh-CN"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\zh-TW\*"; DestDir: "{app}\zh-TW"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\Microsoft.UI.Xaml\Assets\*"; DestDir: "{app}\Microsoft.UI.Xaml\Assets"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\NpuDetect\*"; DestDir: "{app}\NpuDetect"; Flags: ignoreversion skipifsourcedoesntexist

; --- 用户配置：仅在首次安装时写入，升级/卸载都不动 ---
Source: "{#SourceDir}\Core\config.json"; DestDir: "{app}\Core"; Flags: onlyifdoesntexist uninsneveruninstall skipifsourcedoesntexist
Source: "{#SourceDir}\Core\mixin_pre.json"; DestDir: "{app}\Core"; Flags: onlyifdoesntexist uninsneveruninstall skipifsourcedoesntexist
Source: "{#SourceDir}\Core\mixin_post.json"; DestDir: "{app}\Core"; Flags: onlyifdoesntexist uninsneveruninstall skipifsourcedoesntexist

; --- 基础规则集：同样只补齐缺失项，避免覆盖用户后续下载/自定义的版本 ---
Source: "{#SourceDir}\rulesets\*.json"; DestDir: "{app}\rulesets"; Flags: onlyifdoesntexist uninsneveruninstall skipifsourcedoesntexist

; --- 内核：with-core 才打包；no-core 完全不触碰，覆盖安装时原文件原样保留 ---
#if IncludeCore
Source: "{#SourceDir}\Core\sing-box.exe"; DestDir: "{app}\Core"; Flags: ignoreversion skipifsourcedoesntexist
#endif

[Icons]
Name: "{group}\SingBoxWinUI"; Filename: "{app}\SingBoxWinUI.exe"
Name: "{autodesktop}\SingBoxWinUI"; Filename: "{app}\SingBoxWinUI.exe"; Tasks: desktopicon

[Registry]
; HKCU Run 自启；卸载时只删除本安装包写入的值。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SingBoxWinUI"; ValueData: """{app}\SingBoxWinUI.exe"" --autorun"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; 结束页复选框；/VERYSILENT 下自动跳过，不抢焦点。
Filename: "{app}\SingBoxWinUI.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent
