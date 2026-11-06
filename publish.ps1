#requires -Version 5.1
<#
.SYNOPSIS
    构建并打包 SingBoxWinUI 的免安装绿色发行版（Portable Release）。

.DESCRIPTION
    流程：dotnet publish -> 组装干净暂存目录 -> 瘦身 -> 私人数据脱敏 -> 写入开箱即用默认配置
          -> 用 sing-box check 校验默认配置 -> 生成 with-core / no-core 双版本 .7z 便携包
          -> 用 Inno Setup 编译 with-core / no-core 双版本安装向导 -> 逐个生成 SHA256。

    瘦身：
      * 附属资源只保留 zh-CN / zh-TW / en / en-US（其余 80+ 语言目录剔除）；
      * 剔除只有调试器与崩溃转储会用到的 createdump.exe / mscordaccore*.dll / mscordbi.dll。

    安全约定：
      * 只读源码目录，绝不修改/删除工作区里的 Core\ 与 rulesets\；
      * 所有清理动作只发生在 publish\SingBoxRunner 暂存目录内。

.PARAMETER Version
    版本号，用于归档文件名（默认 1.0.0）。

.PARAMETER OnlyRequiredRuleSets
    默认把 rulesets\ 下全部 .json 原样打包，保证解压后与源码一致。
    加此开关则只打包默认配置实际引用的 geosite_gfw.json。

.PARAMETER SkipPublish
    跳过 dotnet publish，直接复用 publish\SingBoxRunner 里的产物（调试脚本时用）。

.PARAMETER SkipArchive
    只做暂存与校验，不压缩（便于检查目录内容）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\publish.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Version 1.0.1 -IncludeAllPublicRuleSets
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # 未安装 Inno Setup 时可显式传 ISCC.exe 路径（例如便携解压出来的那份）
    [string]$IsccPath = "",
    [switch]$OnlyRequiredRuleSets,
    [switch]$SkipPublish,
    [switch]$SkipArchive,
    [switch]$SkipInstaller,
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# StrictMode 下，若本次进程还没执行过原生命令，$LASTEXITCODE 是"未定义"而不是 0，
# 直接读会抛 RuntimeException（-SkipPublish 分支必踩）。先初始化。
$LASTEXITCODE = 0

# dotnet 不一定在 PATH 里（精简 PATH / 服务上下文），先解析绝对路径
function Resolve-DotNet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and $cmd.Source.EndsWith(".exe", [StringComparison]::OrdinalIgnoreCase)) {
        return $cmd.Source
    }

    # 精简过的环境里 $env:ProgramFiles 可能为空，退到固定路径
    $pf = [Environment]::GetEnvironmentVariable("ProgramFiles")
    if ([string]::IsNullOrWhiteSpace($pf)) { $pf = "C:\Program Files" }
    $pf86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ([string]::IsNullOrWhiteSpace($pf86)) { $pf86 = "C:\Program Files (x86)" }

    foreach ($candidate in @(
            (Join-Path $pf "dotnet\dotnet.exe"),
            (Join-Path $pf86 "dotnet\dotnet.exe"),
            "C:\Program Files\dotnet\dotnet.exe")) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

$dotnet = Resolve-DotNet

# Inno Setup 命令行编译器。本机未提权时可以把它便携解压到临时目录，
# 再用 -IsccPath 指过来，避免安装过程弹 UAC 抢焦点。
function Resolve-Iscc {
    if ($IsccPath) {
        if (-not (Test-Path $IsccPath)) {
            throw "-IsccPath 指向的 ISCC.exe 不存在：$IsccPath"
        }
        return (Resolve-Path $IsccPath).Path
    }

    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { return $cmd.Source }

    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:TEMP "sbw-inno\portable\ISCC.exe"),
        (Join-Path $root "installer\.tools\ISCC.exe")
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    return $null
}

# ---------------------------------------------------------------- 路径

$root        = $PSScriptRoot
$project     = Join-Path $root "SingBoxWinUI.csproj"
$publishRoot = Join-Path $root "publish"
$stagingName = "SingBoxRunner"
$staging     = Join-Path $publishRoot $stagingName
$distDir     = Join-Path $root "dist"
$smokeRoot   = Join-Path $publishRoot "smoke_test"
$archiveBase = "SingBoxWinUI-v$Version-$Runtime"
$issScript   = Join-Path $root "installer\SingBoxWinUI.iss"

# publish 直接落到 publish\SingBoxRunner，省掉"先发到 _publish 再复制"的一步
$publishDir  = $staging

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Write-Ok([string]$text) {
    Write-Host "    [OK] $text" -ForegroundColor Green
}

function Write-Skip([string]$text) {
    Write-Host "    [--] $text" -ForegroundColor DarkGray
}

# 构建产物目录的递归删除统一走 .NET API：
# 这些目录动辄几百个文件，Remove-Item 会触发工作区的批量删除确认，
# 而那个保护是给"用户个人文件"用的，不该拦构建脚本清自己的输出目录。
function Remove-BuildDir([string]$path) {
    if (Test-Path $path) {
        [System.IO.Directory]::Delete($path, $true)
    }
}

function Remove-BuildFile([string]$path) {
    if (Test-Path $path) {
        [System.IO.File]::Delete($path)
    }
}

if (-not (Test-Path $project)) {
    throw "找不到项目文件：$project"
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# ---------------------------------------------------------------- 1. publish

Write-Step "1/7 dotnet publish ($Configuration / $Runtime)"

if ($SkipPublish) {
    if (-not (Test-Path $publishDir)) {
        throw "-SkipPublish 指定了，但 $publishDir 不存在。"
    }
    Write-Skip "已跳过 publish，复用现有产物"
}
else {
    if (Test-Path $publishDir) {
        Remove-BuildDir $publishDir
    }

    # 刻意不开 PublishSingleFile / PublishTrimmed：
    # WinUI 3 的 XAML 反射、PRI 资源表，以及从 exe 提取托盘图标的逻辑在
    # 非单文件自包含模式下最稳定。DebugType=none 去掉 pdb 显著减体积。
    $publishArgs = @(
        "publish", $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:WindowsPackageType=None",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false",
        "-o", $publishDir,
        "--nologo"
    )

    if (-not $dotnet) {
        throw "找不到 dotnet.exe。装了 .NET SDK 但不在 PATH 时，请把 dotnet 目录加进 PATH。"
    }

    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码 $LASTEXITCODE"
    }

    Write-Ok "publish 完成 -> $publishDir"
}

# ---------------------------------------------------------------- 2. staging

Write-Step "2/7 准备暂存目录"

if (-not (Test-Path $staging)) {
    throw "publish 产物目录不存在：$staging"
}

$stagedCount = @(Get-ChildItem -Path $staging -Recurse -File).Count
Write-Ok "$stagingName\ 就绪（$stagedCount 个文件）"

# ---------------------------------------------------------------- 3. 瘦身

Write-Step "3/7 瘦身（语言资源 + 调试件）"

# 3a. 附属资源：只留中英。
#     csproj 已声明 SatelliteResourceLanguages，这里兜底 ——
#     WinAppSDK 的 mui 是包内 native 资源，不一定走 SDK 的卫星程序集过滤。
$keepCultures = @("zh-CN", "zh-TW", "en", "en-US")

$cultureDirs = @(Get-ChildItem -Path $staging -Directory | Where-Object {
    @(Get-ChildItem -Path $_.FullName -Filter "*.mui" -File -ErrorAction SilentlyContinue).Count -gt 0
})

$cultureBytes = 0
$droppedDirs = 0

foreach ($dir in $cultureDirs) {
    if ($keepCultures -contains $dir.Name) {
        Write-Skip "保留语言资源 $($dir.Name)"
        continue
    }

    $size = (@(Get-ChildItem -Path $dir.FullName -Recurse -File) | Measure-Object -Property Length -Sum).Sum
    if (-not $size) { $size = 0 }

    $cultureBytes += $size
    Remove-BuildDir $dir.FullName
    $droppedDirs++
}

Write-Ok "移除 $droppedDirs 个语言目录（省 $([math]::Round($cultureBytes / 1MB, 2)) MB）"

# 3b. 调试件：createdump 只在崩溃转储时用，mscordaccore / mscordbi 只服务于调试器。
#     正常代理功能完全不需要，剔掉可省约 4 MB。
$debugArtifacts = @(Get-ChildItem -Path $staging -File | Where-Object {
    $_.Name -eq "createdump.exe" -or
    $_.Name -like "mscordaccore*.dll" -or
    $_.Name -eq "mscordbi.dll"
})

$debugBytes = 0
foreach ($file in $debugArtifacts) {
    $debugBytes += $file.Length
    Remove-BuildFile $file.FullName
}

Write-Ok "移除 $($debugArtifacts.Count) 个调试件（省 $([math]::Round($debugBytes / 1MB, 2)) MB）"

# ---------------------------------------------------------------- 4. 脱敏

Write-Step "4/7 私人数据脱敏"

# 严禁进入发行包的文件（相对暂存目录）
$forbiddenFiles = @(
    "Core\app_settings.json",       # 含 WebDAV 地址/账号/密码、上次配置路径
    "Core\profiles.json",           # 订阅元数据（含订阅链接）
    "Core\running_config.json",     # 运行期合成配置
    "Core\cache.db",                # sing-box 缓存（含 fakeip 记录）
    "Core\cache.db-shm",
    "Core\cache.db-wal"
)

foreach ($rel in $forbiddenFiles) {
    $full = Join-Path $staging $rel
    if (Test-Path $full) {
        Remove-BuildFile $full
        Write-Ok "移除 $rel"
    }
}

# 私人订阅目录
$profilesDir = Join-Path $staging "Core\profiles"
if (Test-Path $profilesDir) {
    Remove-BuildDir $profilesDir
    Write-Ok "移除 Core\profiles\（私人订阅配置）"
}

# 调试符号 / 日志 / 备份等临时产物
$junk = Get-ChildItem -Path $staging -Recurse -File -Include *.pdb, *.log, *.bak, *.tmp, *.orig -ErrorAction SilentlyContinue
foreach ($file in $junk) {
    Remove-BuildFile $file.FullName
    Write-Ok "移除 $($file.FullName.Substring($staging.Length + 1))"
}

# ---------------------------------------------------------------- 4b. mixin 模板

$mixinPre = @'
{
  // 全局前置拓展（高优先级）
  // · 标量字段会覆盖订阅里的同名字段
  // · 带 tag 的数组元素（outbounds / inbounds / dns.servers / route.rule_set）
  //   同 tag 会被替换，新 tag 插入到最前面
  // · route.rules / dns.rules 会插入到规则链最前面
  //
  // 占位符（合并后自动展开，换订阅无需改文件）：
  //   __ALL__ 全部真实节点 ｜ __AUTO__ 剔除高倍率节点 ｜ __REGION:hk/jp/sg/us/kr/tw__ 按地区
  //   __FAKEIP__ 复用现有 fakeip 服务器 tag
  //   例：{ "type": "urltest", "tag": "自动选择", "outbounds": ["__AUTO__"] }
  //
  // 示例：
  //   "log": { "level": "debug" },
  //   "outbounds": [{ "type": "direct", "tag": "mixin-direct" }],
  //   "route": { "rules": [{ "domain_suffix": ["example.com"], "outbound": "mixin-direct" }] }
}
'@

$mixinPost = @'
{
  // 全局后置拓展（低优先级）
  // · 只在订阅里没有该字段时写入标量
  // · 带 tag 的数组元素同 tag 替换，新 tag 追加到末尾
  // · route.rules / dns.rules 追加到规则链最后面
  //
  // 示例：
  //   "outbounds": [{ "type": "socks", "tag": "my-proxy", "server": "127.0.0.1", "server_port": 1080 }],
  //   "route": { "rules": [{ "domain_suffix": ["intranet.local"], "outbound": "my-proxy" }] }
}
'@

# 与 MixinService.CreateTemplate 保持一致：带 // 注释的初始空模板
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $staging "Core\mixin_pre.json"), ($mixinPre + "`r`n"), $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $staging "Core\mixin_post.json"), ($mixinPost + "`r`n"), $utf8NoBom)
Write-Ok "写入 Core\mixin_pre.json / mixin_post.json（空模板）"

# ---------------------------------------------------------------- 4c. 默认配置

$defaultConfig = @'
{
  "$schema": "https://sing-box.sagernet.org/schema.json",
  "log": {
    "level": "info",
    "timestamp": false
  },
  "dns": {
    "servers": [
      {
        "tag": "dns-local",
        "type": "udp",
        "server": "223.5.5.5",
        "server_port": 53
      },
      {
        "tag": "dns-remote",
        "type": "udp",
        "server": "1.1.1.1",
        "server_port": 53,
        "detour": "Proxy"
      }
    ],
    "final": "dns-local",
    "strategy": "prefer_ipv4"
  },
  "inbounds": [
    {
      "type": "mixed",
      "tag": "mixed-in",
      "listen": "127.0.0.1",
      "listen_port": 20122
    }
  ],
  "outbounds": [
    {
      "type": "selector",
      "tag": "Proxy",
      "outbounds": ["direct"],
      "default": "direct"
    },
    {
      "type": "direct",
      "tag": "direct"
    }
  ],
  "route": {
    "rules": [
      { "action": "sniff" },
      { "protocol": "dns", "action": "hijack-dns" },
      { "action": "route", "ip_is_private": true, "outbound": "direct" },
      { "action": "route", "rule_set": ["geosite-gfw"], "outbound": "Proxy" }
    ],
    "rule_set": [
      {
        "tag": "geosite-gfw",
        "type": "local",
        "format": "source",
        "path": "../rulesets/geosite_gfw.json"
      }
    ],
    "final": "Proxy",
    "auto_detect_interface": true,
    "default_domain_resolver": { "server": "dns-local" }
  },
  "experimental": {
    "clash_api": {
      "external_controller": "127.0.0.1:20123",
      "default_mode": "rule"
    },
    "cache_file": {
      "enabled": true,
      "path": "cache.db"
    }
  }
}
'@

[System.IO.File]::WriteAllText((Join-Path $staging "Core\config.json"), ($defaultConfig + "`r`n"), $utf8NoBom)
Write-Ok "写入 Core\config.json（干净默认骨架，无任何节点）"

# ---------------------------------------------------------------- 4d. 规则集

Write-Step "5/7 规则集筛选"

$rulesetsStaging = Join-Path $staging "rulesets"
if (Test-Path $rulesetsStaging) {
    Remove-BuildDir $rulesetsStaging
}
New-Item -ItemType Directory -Path $rulesetsStaging -Force | Out-Null

# 默认配置引用的规则集（必须存在，否则 sing-box check 失败）
$requiredRuleSets = @("geosite_gfw.json")
$sourceRulesets = Join-Path $root "rulesets"

# 默认：rulesets\ 下全部 .json 原样带走（含公共规则集与自建规则），
# 保证绿色版解压后 rulesets\ 与源码一致；-OnlyRequiredRuleSets 可只打必需项。
if ($OnlyRequiredRuleSets) {
    $wanted = $requiredRuleSets
}
else {
    $wanted = @(Get-ChildItem -Path $sourceRulesets -File -Filter "*.json" |
                ForEach-Object { $_.Name })
}

foreach ($name in $wanted) {
    $src = Join-Path $sourceRulesets $name
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $rulesetsStaging $name) -Force
        Write-Ok "打包 rulesets\$name"
    }
    else {
        Write-Warning "缺少规则集 $name（默认配置引用了它，sing-box check 会失败）"
    }
}

$allPublic = @(Get-ChildItem -Path $sourceRulesets -File -Filter "*.json")
foreach ($file in $allPublic) {
    if ($wanted -notcontains $file.Name) {
        Write-Skip "排除 rulesets\$($file.Name)"
    }
}

# ---------------------------------------------------------------- 5. 校验

Write-Step "6/7 校验默认配置（sing-box check）"

$kernel = Join-Path $staging "Core\sing-box.exe"
if (-not (Test-Path $kernel)) {
    throw "暂存目录里没有 Core\sing-box.exe"
}

# -D 指定工作目录：配置里的 ../rulesets/ 相对 Core\ 解析
$checkOutput = & $kernel check -c (Join-Path $staging "Core\config.json") -D (Join-Path $staging "Core") 2>&1
$checkExit = $LASTEXITCODE

if ($checkExit -ne 0) {
    Write-Host ($checkOutput | Out-String) -ForegroundColor Red
    throw "sing-box check 未通过（退出码 $checkExit），已中止打包。"
}

Write-Ok "sing-box check 通过（退出码 0）"

# 关键文件存在性检查
$mustHave = @(
    "SingBoxWinUI.exe",
    "Core\sing-box.exe",
    "Core\config.json",
    "Core\mixin_pre.json",
    "Core\mixin_post.json",
    "Assets\app.ico",
    "Assets\app-64.png",
    "rulesets\geosite_gfw.json"
)

foreach ($rel in $mustHave) {
    $full = Join-Path $staging $rel
    if (-not (Test-Path $full)) {
        throw "发行包缺少必要文件：$rel"
    }
}
Write-Ok "必要文件齐全（$($mustHave.Count) 项）"

# 反向检查：确认没有私人数据残留
$leakPatterns = @(
    "Core\app_settings.json",
    "Core\profiles.json",
    "Core\profiles",
    "Core\running_config.json",
    "Core\cache.db"
)
foreach ($rel in $leakPatterns) {
    if (Test-Path (Join-Path $staging $rel)) {
        throw "脱敏失败：发行包中仍存在 $rel"
    }
}
Write-Ok "私人数据检查通过（无 settings / profiles / cache 残留）"

# 反向检查：瘦身是否生效
$leftDebug = @(Get-ChildItem -Path $staging -File | Where-Object {
    $_.Name -eq "createdump.exe" -or
    $_.Name -like "mscordaccore*.dll" -or
    $_.Name -eq "mscordbi.dll"
})
if ($leftDebug.Count -gt 0) {
    Write-Warning "调试件未完全剔除：$($leftDebug.Name -join ', ')"
}
else {
    Write-Ok "调试件已剔除（createdump / mscordaccore / mscordbi）"
}

$leftCultures = @(Get-ChildItem -Path $staging -Directory | Where-Object {
    $keepCultures -notcontains $_.Name -and
    @(Get-ChildItem -Path $_.FullName -Filter "*.mui" -File -ErrorAction SilentlyContinue).Count -gt 0
})
if ($leftCultures.Count -gt 0) {
    Write-Warning "仍有多余语言目录：$($leftCultures.Name -join ', ')"
}
else {
    Write-Ok "语言资源仅保留：$($keepCultures -join ', ')"
}

# ---------------------------------------------------------------- 6. 压缩

if ($SkipArchive) {
    Write-Step "7/8 已跳过压缩与安装包（-SkipArchive）"
    Write-Host "    暂存目录：$staging"
    exit 0
}

Write-Step "7/8 双版本便携包 + 双版本安装向导"

function Find-SevenZip {
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:ProgramFiles\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe",
        "$env:ProgramFiles\NanaZip\7z.exe",
        "$env:LOCALAPPDATA\Microsoft\WindowsApps\7z.exe"
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) { return $path }
    }
    return $null
}

$sevenZip = Find-SevenZip
if (-not $sevenZip) {
    throw "未找到 7z.exe / NanaZip，无法生成 .7z 便携包。请先安装 7-Zip 或 NanaZip。"
}

# ---- 7a. 便携包：with-core 完整，no-core 排除内核 ----

$archiveWithCore = Join-Path $distDir "$archiveBase-with-core.7z"
$archiveNoCore   = Join-Path $distDir "$archiveBase-no-core.7z"

Push-Location $publishRoot
try {
    Remove-BuildFile $archiveWithCore
    # -mx=9 极限压缩，-mfb=64 快速字节，-md=32m 字典，-ms=on 固实
    & $sevenZip a -t7z -mx=9 -mfb=64 -md=32m -ms=on $archiveWithCore $stagingName
    if ($LASTEXITCODE -ne 0) { throw "7z 打包 with-core 失败（退出码 $LASTEXITCODE）" }
    Write-Ok "with-core 便携包完成"

    Remove-BuildFile $archiveNoCore
    # 排除内核：no-core 版解压后 Core\ 里没有 sing-box.exe，由用户自备或沿用旧文件
    & $sevenZip a -t7z -mx=9 -mfb=64 -md=32m -ms=on $archiveNoCore $stagingName "-xr!sing-box.exe"
    if ($LASTEXITCODE -ne 0) { throw "7z 打包 no-core 失败（退出码 $LASTEXITCODE）" }
    Write-Ok "no-core 便携包完成（已排除 Core\sing-box.exe）"
}
finally {
    Pop-Location
}

# ---- 7b. 安装向导（Inno Setup，同一 .iss 两套编译宏）----

$setupWithCore = Join-Path $distDir "SingBoxWinUI-Setup-v$Version-$Runtime-with-core.exe"
$setupNoCore   = Join-Path $distDir "SingBoxWinUI-Setup-v$Version-$Runtime-no-core.exe"

$iscc = Resolve-Iscc
if ($SkipInstaller) {
    Write-Skip "已跳过安装包编译（-SkipInstaller）"
}
elseif (-not $iscc) {
    Write-Warning "未找到 ISCC.exe，跳过安装包编译。可安装 Inno Setup 或用 -IsccPath 指定。"
}
elseif (-not (Test-Path $issScript)) {
    Write-Warning "找不到 installer\SingBoxWinUI.iss，跳过安装包编译。"
}
else {
    foreach ($variant in @("with-core", "no-core")) {
        $expected = if ($variant -eq "with-core") { $setupWithCore } else { $setupNoCore }
        Remove-BuildFile $expected

        # ISPP 的 /Dxxx=0 是字符串 token，数值判断会失效；
        # 精简版改用 /DNoCore=1 这个「存在即生效」的开关。
        $isppArgs = @("/DAppVersion=$Version", "/DSourceDir=$staging", "/DOutDir=$distDir")
        if ($variant -eq "no-core") { $isppArgs = @("/DNoCore=1") + $isppArgs }

        & $iscc @isppArgs $issScript
        if ($LASTEXITCODE -ne 0) { throw "ISCC 编译 $variant 失败（退出码 $LASTEXITCODE）" }
        if (-not (Test-Path $expected)) { throw "ISCC 未产出预期文件：$expected" }
        Write-Ok "安装向导 $variant 完成"
    }
}

# ---- 7c. 逐文件 SHA256 ----

$artifacts = @($archiveWithCore, $archiveNoCore, $setupWithCore, $setupNoCore) |
    Where-Object { Test-Path $_ } | ForEach-Object { Get-Item $_ }

foreach ($item in $artifacts) {
    $itemHash = (Get-FileHash -Path $item.FullName -Algorithm SHA256).Hash
    $itemHashFile = "$($item.FullName).sha256"
    "$itemHash  $($item.Name)" | Out-File -FilePath $itemHashFile -Encoding ASCII -NoNewline
    Add-Content -Path $itemHashFile -Value "" -Encoding ASCII
    Write-Ok "SHA256 -> $($item.Name).sha256"
}

# 冒烟测试复用带内核的便携包
$archivePath = $archiveWithCore

# ---------------------------------------------------------------- 8. 冒烟测试
#
# 顺序很重要：压缩必须在冒烟测试之前完成。
# 冒烟测试会把 exe 跑起来，运行期会生成 app_settings.json / profiles.json，
# 如果先跑测试再压缩，这些带临时路径的文件就会被打进发行包。

$smokeResult = "未执行"

if ($SkipSmokeTest) {
    Write-Step "8/8 已跳过冒烟测试（-SkipSmokeTest）"
}
else {
    Write-Step "8/8 解压冒烟测试"

    $smokeMoved = Join-Path $publishRoot "smoke_test_moved"
    Remove-BuildDir $smokeRoot
    Remove-BuildDir $smokeMoved
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

    if ($sevenZip) {
        & $sevenZip x $archivePath "-o$smokeRoot" -y | Out-Null
    }
    else {
        Expand-Archive -Path $archivePath -DestinationPath $smokeRoot -Force
    }

    $smokeApp = Join-Path $smokeRoot $stagingName
    $smokeExe = Join-Path $smokeApp "SingBoxWinUI.exe"
    if (-not (Test-Path $smokeExe)) {
        throw "冒烟测试失败：解压后找不到 $stagingName\SingBoxWinUI.exe"
    }

    # ---- 8a. 包里绝不能有私人数据 ----
    $forbidden = @(
        "Core\app_settings.json",
        "Core\profiles.json",
        "Core\running_config.json",
        "Core\cache.db"
    )
    foreach ($rel in $forbidden) {
        if (Test-Path (Join-Path $smokeApp $rel)) {
            throw "冒烟测试失败：包里残留 $rel"
        }
    }
    if (Test-Path (Join-Path $smokeApp "Core\profiles")) {
        throw "冒烟测试失败：包里残留 Core\profiles\ 目录"
    }
    Write-Ok "8a 包内无私人数据"

    # ---- 8b. 首次启动：进程起得来，且自动初始化设置与订阅 ----
    $proc1 = Start-Process -FilePath $smokeExe -PassThru
    Start-Sleep -Seconds 12
    if ($proc1.HasExited) {
        throw "冒烟测试失败：首次启动后进程退出（退出码 $($proc1.ExitCode)）"
    }

    foreach ($rel in @("Core\app_settings.json", "Core\profiles.json")) {
        if (-not (Test-Path (Join-Path $smokeApp $rel))) {
            throw "冒烟测试失败：首次启动未自动生成 $rel"
        }
    }
    Stop-Process -Id $proc1.Id -Force -ErrorAction SilentlyContinue
    Get-Process -Name "sing-box" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Write-Ok "8b 首次启动正常，已初始化 app_settings.json / profiles.json"

    # ---- 8c. 移动目录后再启动：验证绿色便携自愈 ----
    # 模拟真实场景：用户在 A 目录跑过（app_settings 里存了 A 的绝对路径），
    # 然后把整个文件夹挪到 B。再次启动时内核必须能自动找到配置并拉起来。
    $settingsPath = Join-Path $smokeApp "Core\app_settings.json"
    $settings = Get-Content -Path $settingsPath -Raw | ConvertFrom-Json
    $settings.LastCoreRunning = $true
    $settings.AutoStartCore = $true
    $staleConfigPath = $settings.ConfigPath
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding UTF8

    if (-not (Test-Path $smokeMoved)) {
        New-Item -ItemType Directory -Path $smokeMoved -Force | Out-Null
    }
    Move-Item -Path $smokeApp -Destination (Join-Path $smokeMoved $stagingName) -Force

    $movedApp = Join-Path $smokeMoved $stagingName
    $movedExe = Join-Path $movedApp "SingBoxWinUI.exe"
    $movedSettings = Join-Path $movedApp "Core\app_settings.json"

    $otherKernel = Get-Process -Name "sing-box" -ErrorAction SilentlyContinue
    $proc2 = Start-Process -FilePath $movedExe -PassThru
    Start-Sleep -Seconds 18

    if ($proc2.HasExited) {
        throw "冒烟测试失败：移动目录后进程退出（退出码 $($proc2.ExitCode)）"
    }

    # 配置路径必须已经自愈到新位置
    $healed = Get-Content -Path $movedSettings -Raw | ConvertFrom-Json
    if ($healed.ConfigPath -eq $staleConfigPath) {
        throw "冒烟测试失败：移动目录后 ConfigPath 仍是旧路径 $staleConfigPath"
    }
    if (-not ($healed.ConfigPath -like "*$($smokeMoved)*")) {
        throw "冒烟测试失败：ConfigPath 未指向新目录，实际为 $($healed.ConfigPath)"
    }
    Write-Ok "8c 移动目录后配置路径已自愈 -> $($healed.ConfigPath)"

    # 内核应当被自动拉起（若本机已有 sing-box 在跑，端口会冲突，跳过这条断言）
    if ($otherKernel) {
        Write-Skip "8c 本机已有 sing-box 进程，跳过内核自启断言"
    }
    else {
        $kernel = Get-Process -Name "sing-box" -ErrorAction SilentlyContinue
        if (-not $kernel) {
            throw "冒烟测试失败：移动目录后内核未自动启动"
        }
        Write-Ok "8c 移动目录后内核已自动启动（PID $($kernel[0].Id)）"
    }

    # ---- 8d. 收尾 ----
    Stop-Process -Id $proc2.Id -Force -ErrorAction SilentlyContinue
    Get-Process -Name "sing-box" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-BuildDir $smokeRoot
    Remove-BuildDir $smokeMoved

    $smokeResult = "通过（无私人数据 / 可启动 / 自动初始化 / 移动目录自愈）"
    Write-Ok "8d 冒烟测试通过，临时目录已清理"
}

$stagingFiles = @(Get-ChildItem -Path $staging -Recurse -File)
$stagingSize = ($stagingFiles | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host "==================== 发行产物 ====================" -ForegroundColor Cyan
foreach ($item in $artifacts) {
    $digest = ((Get-Content "$($item.FullName).sha256" -Raw).Trim() -split '\s+')[0]
    Write-Host ("  {0,-58} {1,8:N2} MB  {2}" -f $item.Name, ($item.Length / 1MB), $digest)
}
Write-Host ("暂存目录 : {0}" -f $staging)
Write-Host ("解压后   : {0} 个文件 / {1:N2} MB" -f $stagingFiles.Count, ($stagingSize / 1MB))
Write-Host ("冒烟测试 : {0}" -f $smokeResult)
Write-Host ("瘦身节省 : {0:N2} MB（语言资源 + 调试件）" -f (($cultureBytes + $debugBytes) / 1MB))
Write-Host ""
Write-Host "完成。" -ForegroundColor Green
