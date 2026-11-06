# 轻量模式内存对比采样脚本
#
# 用法：
#   pwsh -File scripts\measure-memory.ps1                 # 采一次快照
#   pwsh -File scripts\measure-memory.ps1 -Watch 120      # 连续采样 120 秒，每 5 秒一行
#
# 典型对比流程：
#   1) 启动软件、保持窗口可见、内核运行中 -> 采一次（基线）
#   2) 点关闭让窗口进托盘，等到设定的轻量延迟过去 -> 再采一次（轻量态）
#   3) 用 -Watch 能看到工作集在进入轻量模式那一刻掉下去

param(
    [string]$AppName = 'SingBoxWinUI',
    [string]$KernelName = 'sing-box',
    [int]$Watch = 0,
    [int]$IntervalSeconds = 5
)

function Get-Snapshot {
    $rows = @()

    foreach ($name in @($AppName, $KernelName)) {
        $proc = Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $proc) {
            $rows += [pscustomobject]@{
                Process = $name
                Pid     = '-'
                WS_MB   = '-'
                Priv_MB = '-'
                Threads = '-'
            }
            continue
        }

        $rows += [pscustomobject]@{
            Process = $name
            Pid     = $proc.Id
            WS_MB   = [math]::Round($proc.WorkingSet64 / 1MB, 1)
            Priv_MB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
            Threads = $proc.Threads.Count
        }
    }

    return $rows
}

if ($Watch -le 0) {
    Get-Snapshot | Format-Table -AutoSize
    return
}

$deadline = (Get-Date).AddSeconds($Watch)
while ((Get-Date) -lt $deadline) {
    $stamp = (Get-Date).ToString('HH:mm:ss')
    $snap = Get-Snapshot
    $app = $snap | Where-Object Process -eq $AppName
    $ker = $snap | Where-Object Process -eq $KernelName

    '{0}  UI {1,7} MB / {2,7} MB   kernel {3,7} MB / {4,7} MB' -f `
        $stamp, $app.WS_MB, $app.Priv_MB, $ker.WS_MB, $ker.Priv_MB

    Start-Sleep -Seconds $IntervalSeconds
}
