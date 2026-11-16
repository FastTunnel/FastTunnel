# FastTunnel（HTTP 穿透） vs frp（type=tcp）逐级对照：吞吐、延迟、错误率、CPU、内存
#
# 为什么单独一个脚本：主实验脚本只跑 frp 的 http 代理；这里要跑 frp 的 tcp 代理，
# 并且需要「两个框架交替测」以抵消机器漂移，同时采集进程内存（私有字节 / 峰值工作集）。
#
# 用法：
#   powershell -File bench\exp\compare-tcp.ps1
#   powershell -File bench\exp\compare-tcp.ps1 -LevelsCsv 5000,10000,30000,60000,90000 -Rounds 2

param(
    [string]$LevelsCsv = '5000,10000,30000,60000,90000',
    [int]$Connections = 256,
    [int]$Rounds = 1,
    [double]$Warmup = 2,
    [double]$Duration = 5,
    [int]$FrpRemotePort = 17090
)

$ErrorActionPreference = 'Stop'

$levels = @($LevelsCsv -split '[,\s;]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$exp = Split-Path $PSScriptRoot -Parent | Join-Path -ChildPath 'out\exp'
$exp = Join-Path (Split-Path $PSScriptRoot -Parent) 'out\exp'
$run = Join-Path $exp 'run'
$runner = Join-Path (Split-Path $PSScriptRoot -Parent) 'LoadRunner\bin\Release\net10.0\FastTunnel.LoadRunner.exe'
$frpBin = Join-Path $exp 'frp\frp_0.72.0_windows_amd64'
$outDir = Join-Path $exp 'compare-tcp'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$ftPort = 17270
$frpBind = 17000

function Write-Utf8NoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
}

# --- 启动两条链路（都在 8080 源站后面） ---
Write-Host '==> 启动 FastTunnel 与 frp(tcp)' -ForegroundColor Cyan

$ftServer = Start-Process (Join-Path $exp 'ft-server\FastTunnel.Server.exe') `
    -WorkingDirectory (Join-Path $run 'ft-server') -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $outDir 'ft-server.log') -RedirectStandardError (Join-Path $outDir 'ft-server.err')
Start-Sleep -Milliseconds 1200
$ftClient = Start-Process (Join-Path $exp 'ft-client\FastTunnel.Client.exe') `
    -WorkingDirectory (Join-Path $run 'ft-client') -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $outDir 'ft-client.log') -RedirectStandardError (Join-Path $outDir 'ft-client.err')

Write-Utf8NoBom (Join-Path $outDir 'frps.toml') "bindPort=$frpBind`nlog.to=`"disable`"`ntransport.tls.force=false`n"
Write-Utf8NoBom (Join-Path $outDir 'frpc.toml') @"
serverAddr = "127.0.0.1"
serverPort = $frpBind

log.to = "disable"

transport.tls.enable = false
transport.poolCount = $Connections

[[proxies]]
name = "tcp-bench"
type = "tcp"
localIP = "127.0.0.1"
localPort = 8080
remotePort = $FrpRemotePort
"@

$frps = Start-Process (Join-Path $frpBin 'frps.exe') -ArgumentList @('-c', (Join-Path $outDir 'frps.toml')) `
    -WorkingDirectory $outDir -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $outDir 'frps.log') -RedirectStandardError (Join-Path $outDir 'frps.err')
Start-Sleep -Milliseconds 800
$frpc = Start-Process (Join-Path $frpBin 'frpc.exe') -ArgumentList @('-c', (Join-Path $outDir 'frpc.toml')) `
    -WorkingDirectory $outDir -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $outDir 'frpc.log') -RedirectStandardError (Join-Path $outDir 'frpc.err')

Start-Sleep -Seconds 5

foreach ($probe in @(@{ Port = $ftPort; Host = 'bench.ftbench.net' }, @{ Port = $FrpRemotePort; Host = 'bench.ftbench.net' })) {
    $code = (& curl.exe -s -o NUL -w '%{http_code}' --noproxy '*' --max-time 5 -H "Host: $($probe.Host)" "http://127.0.0.1:$($probe.Port)/bench/index.html" 2>$null | Out-String).Trim()
    Write-Host "    port $($probe.Port) -> HTTP $code"
    if ($code -ne '200') { throw "端口 $($probe.Port) 未就绪" }
}

$rows = @()

function Measure-Round {
    param([string]$Framework, [int]$Port, [int]$Level, [System.Diagnostics.Process]$Server, [System.Diagnostics.Process]$Client)

    $outFile = Join-Path $outDir ("{0}-{1}-r{2}.json" -f $Framework, $Level, $script:round)

    $serverCpuBefore = $Server.TotalProcessorTime
    $clientCpuBefore = $Client.TotalProcessorTime

    $runnerArgs = @('--connect', "127.0.0.1:$Port", '--host', 'bench.ftbench.net', '--path', '/bench/index.html',
        '--rps', "$Level", '--connections', "$Connections", '--warmup', "$Warmup", '--duration', "$Duration",
        '--out', $outFile, '--label', "$Framework-$Level")

    & $runner @runnerArgs | Out-Null
    $result = Get-Content $outFile -Raw | ConvertFrom-Json

    $Server.Refresh(); $Client.Refresh()
    $elapsed = [math]::Max(0.001, $result.DurationSeconds + $Warmup)

    [pscustomobject]@{
        Framework   = $Framework
        Round       = $script:round
        TargetRps   = $Level
        Connections = $Connections
        AchievedRps = $result.AchievedRps
        ErrorRate   = [math]::Round($result.ErrorRate * 100, 3)
        P50         = $result.LatencyMs.P50
        P99         = $result.LatencyMs.P99
        ServerCores = [math]::Round(($Server.TotalProcessorTime - $serverCpuBefore).TotalSeconds / $elapsed, 2)
        ClientCores = [math]::Round(($Client.TotalProcessorTime - $clientCpuBefore).TotalSeconds / $elapsed, 2)
        # 私有字节：进程真实占用（不含可共享的映射）；峰值工作集：历史峰值，反映瞬时水位
        ServerMemMB = [math]::Round($Server.PrivateMemorySize64 / 1MB, 1)
        ClientMemMB = [math]::Round($Client.PrivateMemorySize64 / 1MB, 1)
        TotalMemMB  = [math]::Round(($Server.PrivateMemorySize64 + $Client.PrivateMemorySize64) / 1MB, 1)
        ServerPeakMB = [math]::Round($Server.PeakWorkingSet64 / 1MB, 1)
        ClientPeakMB = [math]::Round($Client.PeakWorkingSet64 / 1MB, 1)
    }
}

try {
    for ($script:round = 1; $script:round -le $Rounds; $script:round++) {
        # 奇偶轮交换顺序，抵消"谁先跑谁吃亏"的偏置
        $order = if ($script:round % 2 -eq 1) { @('ft', 'frp-tcp') } else { @('frp-tcp', 'ft') }

        foreach ($level in $levels) {
            foreach ($framework in $order) {
                if ($framework -eq 'ft') {
                    $row = Measure-Round -Framework 'ft' -Port $ftPort -Level $level -Server $ftServer -Client $ftClient
                }
                else {
                    $row = Measure-Round -Framework 'frp-tcp' -Port $FrpRemotePort -Level $level -Server $frps -Client $frpc
                }

                $rows += $row
                Write-Host ("    {0,-8} r{1} {2,7} RPS -> 达成 {3,8}  错误 {4,6}%  p50 {5,7}  p99 {6,8}  内存 {7,6}/{8,6} MB" -f `
                    $row.Framework, $row.Round, $level, $row.AchievedRps, $row.ErrorRate, $row.P50, $row.P99, $row.ServerMemMB, $row.ClientMemMB)
            }
        }
    }
}
finally {
    foreach ($proc in @($frpc, $frps, $ftClient, $ftServer)) {
        if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
}

$csv = Join-Path $outDir 'compare-tcp.csv'
$rows | Export-Csv $csv -NoTypeInformation -Encoding utf8

Write-Host ''
Write-Host '================ 结果（内存=私有字节，服务端/客户端） ================' -ForegroundColor Green
$rows | Format-Table Framework, Round, TargetRps, AchievedRps, ErrorRate, P50, P99, ServerCores, ClientCores, ServerMemMB, ClientMemMB, TotalMemMB -AutoSize
Write-Host "CSV：$csv"
