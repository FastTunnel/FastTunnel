# FastTunnel 最新 NativeAOT 版 vs frp 0.72.0（type=tcp）逐级对照
#
# 和 compare-tcp.ps1 同口径（RPS 阶梯 / 延迟分位 / 错误率 / CPU / 内存，同轮交替），
# 唯一变化：FastTunnel 侧改用 3.6.0 NativeAOT 自包含 exe，而不是 IL 发布版。
# 用来回答"AOT 之后和 frp 的差距"。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File bench\exp\aot-vs-frp.ps1
#   powershell -ExecutionPolicy Bypass -File bench\exp\aot-vs-frp.ps1 -LevelsCsv 5000,10000,30000,60000,90000 -Rounds 2

param(
    [string]$LevelsCsv = '5000,10000,30000,60000,90000',
    [int]$Connections = 256,
    [int]$Rounds = 2,
    [double]$Warmup = 2,
    [double]$Duration = 5,
    [int]$FrpRemotePort = 17090,
    [string]$Tag = 'aot-v360'
)

$ErrorActionPreference = 'Stop'

# 已知的 PS 5.1 环境坑：大小写重复的代理变量会让本机 TcpClient 建连抛"已添加项"
foreach ($n in @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','http_proxy','https_proxy','all_proxy')) {
    Remove-Item "env:$n" -ErrorAction SilentlyContinue
}

$levels = @($LevelsCsv -split '[,\s;]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$bench = Split-Path $PSScriptRoot -Parent
$exp = Join-Path $bench 'out\exp'
$run = Join-Path $exp 'run'
$runner = Join-Path $bench 'LoadRunner\bin\Release\net10.0\FastTunnel.LoadRunner.exe'
$frpBin = Join-Path $exp 'frp\frp_0.72.0_windows_amd64'

# FastTunnel AOT 产物
$ftServerExe = Join-Path $bench 'out\aot-v360-server\FastTunnel.Server.exe'
$ftClientExe = Join-Path $bench 'out\aot-v360-client\FastTunnel.Client.exe'

$outDir = Join-Path $exp "aot-vs-frp-$Tag"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# 单文件 AOT 下 Serilog 无法自动发现 sink 程序集，配置必须带 Serilog:Using 节
# （AOT 产物自带配置就有；bench 的旧配置是给 IL 版写的，没有此节）。
# 这里生成 AOT 专用配置副本：读 bench 配置 -> 注入 Using -> 写到 outDir，
# 不改动原始 IL 配置。FastTunnel 侧工作目录指向这些副本。
$ftServerCwd = Join-Path $outDir 'ft-server'
$ftClientCwd = Join-Path $outDir 'ft-client'
New-Item -ItemType Directory -Path (Join-Path $ftServerCwd 'config') -Force | Out-Null
New-Item -ItemType Directory -Path $ftClientCwd -Force | Out-Null

$ftPort = 17270
$frpBind = 17000

foreach ($f in @($runner, $ftServerExe, $ftClientExe, (Join-Path $frpBin 'frps.exe'), (Join-Path $frpBin 'frpc.exe'))) {
    if (-not (Test-Path $f)) { throw "缺少可执行文件: $f" }
}
$srcServerCfg = Join-Path $run 'ft-server\config\appsettings.json'
$srcClientCfg = Join-Path $run 'ft-client\appsettings.json'
if (-not (Test-Path $srcServerCfg)) { throw "缺少服务端配置: $srcServerCfg" }
if (-not (Test-Path $srcClientCfg)) { throw "缺少客户端配置: $srcClientCfg" }

function Write-Utf8NoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
}

# 注入 Serilog:Using：AOT 只静态链接了 Console sink，File sink 被裁剪，
# 列进去会 FileNotFoundException；必须强制覆盖为仅 Console（而非幂等保留旧值）。
function Add-SerilogUsing([string]$json) {
    $o = $json | ConvertFrom-Json
    if (-not $o.PSObject.Properties['Serilog']) {
        $o | Add-Member -NotePropertyName 'Serilog' -NotePropertyValue ([pscustomobject]@{})
    }
    if ($o.Serilog.PSObject.Properties['Using']) {
        $o.Serilog.Using = @('Serilog.Sinks.Console')
    } else {
        $o.Serilog | Add-Member -NotePropertyName 'Using' -NotePropertyValue @('Serilog.Sinks.Console')
    }
    return ($o | ConvertTo-Json -Depth 20)
}

$srvJson = Add-SerilogUsing ([System.IO.File]::ReadAllText($srcServerCfg, [System.Text.Encoding]::UTF8))
Write-Utf8NoBom (Join-Path $ftServerCwd 'config\appsettings.json') $srvJson
$cliJson = Add-SerilogUsing ([System.IO.File]::ReadAllText($srcClientCfg, [System.Text.Encoding]::UTF8))
Write-Utf8NoBom (Join-Path $ftClientCwd 'appsettings.json') $cliJson
Write-Host "    AOT 配置副本已生成（含 Serilog:Using）"

Write-Host "==> 启动 FastTunnel-AOT 与 frp(tcp)" -ForegroundColor Cyan
Write-Host "    FT server exe: $ftServerExe"
Write-Host "    FT client exe: $ftClientExe"

$ftServer = Start-Process $ftServerExe -WorkingDirectory $ftServerCwd -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $outDir 'ft-server.log') -RedirectStandardError (Join-Path $outDir 'ft-server.err')
Start-Sleep -Milliseconds 1200
$ftClient = Start-Process $ftClientExe -WorkingDirectory $ftClientCwd -PassThru -WindowStyle Hidden `
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
    if ($code -ne '200') { throw "端口 $($probe.Port) 未就绪（HTTP $code）" }
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
    $result = Get-Content $outFile -Raw -Encoding UTF8 | ConvertFrom-Json

    $Server.Refresh(); $Client.Refresh()
    $elapsed = [math]::Max(0.001, $result.DurationSeconds + $Warmup)

    [pscustomobject]@{
        Framework    = $Framework
        Round        = $script:round
        TargetRps    = $Level
        Connections  = $Connections
        AchievedRps  = $result.AchievedRps
        ErrorRate    = [math]::Round($result.ErrorRate * 100, 3)
        P50          = $result.LatencyMs.P50
        P90          = $result.LatencyMs.P90
        P99          = $result.LatencyMs.P99
        ServerCores  = [math]::Round(($Server.TotalProcessorTime - $serverCpuBefore).TotalSeconds / $elapsed, 2)
        ClientCores  = [math]::Round(($Client.TotalProcessorTime - $clientCpuBefore).TotalSeconds / $elapsed, 2)
        ServerMemMB  = [math]::Round($Server.PrivateMemorySize64 / 1MB, 1)
        ClientMemMB  = [math]::Round($Client.PrivateMemorySize64 / 1MB, 1)
        TotalMemMB   = [math]::Round(($Server.PrivateMemorySize64 + $Client.PrivateMemorySize64) / 1MB, 1)
    }
}

try {
    for ($script:round = 1; $script:round -le $Rounds; $script:round++) {
        $order = if ($script:round % 2 -eq 1) { @('ft-aot', 'frp-tcp') } else { @('frp-tcp', 'ft-aot') }
        foreach ($level in $levels) {
            foreach ($framework in $order) {
                if ($framework -eq 'ft-aot') {
                    $row = Measure-Round -Framework 'ft-aot' -Port $ftPort -Level $level -Server $ftServer -Client $ftClient
                } else {
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
        if ($proc) { try { $proc.Kill() } catch {} }
    }
}

$csv = Join-Path $outDir 'aot-vs-frp.csv'
$rows | Export-Csv $csv -NoTypeInformation -Encoding utf8
$json = Join-Path $outDir 'aot-vs-frp.json'
$rows | ConvertTo-Json -Depth 5 | Write-Utf8NoBom $json

Write-Host ''
Write-Host '================ 结果（内存=私有字节，服务端/客户端） ================' -ForegroundColor Green
$rows | Format-Table Framework, Round, TargetRps, AchievedRps, ErrorRate, P50, P99, ServerCores, ClientCores, TotalMemMB -AutoSize
Write-Host "CSV：$csv"
Write-Host "JSON：$json"
