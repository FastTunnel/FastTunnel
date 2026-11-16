# FastTunnel vs frp v0.72.0 本地 HTTP 穿透对照压测
#
# 源站：默认读 bench\out\exp\env.json（由 setup-iis-admin.ps1 生成，指向 IIS:8080）；
#       没有 env.json 时自动拉起内置静态源站（127.0.0.1:18080）用于验证链路。
# 被测：FastTunnel（mux 数据面）与 frp v0.72.0（http 类型），两侧都关闭 TLS、日志降到指定级别。
# 压测：自家 FastTunnel.LoadRunner，开环定速，每级 record 延迟分位/错误率/达成速率。
#
# 用法：
#   powershell -File bench\exp\run-experiment.ps1
#   powershell -File bench\exp\run-experiment.ps1 -LevelsCsv 5000,10000,15000 -Duration 15
#   powershell -File bench\exp\run-experiment.ps1 -Frameworks ft -LogLevel Debug   # 排查链路
#
# 产物：bench\out\exp\results\*.json、results.csv、report.md

param(
    [string]$OriginHost = '127.0.0.1',
    [int]$OriginPort = 0,
    [string]$OriginPath = '',
    [string]$LevelsCsv = '5000,10000,15000,20000,25000,30000',
    [int]$Connections = 256,
    [switch]$AutoConnections,
    [double]$Warmup = 3,
    [double]$Duration = 9,
    [string[]]$Frameworks = @('ft', 'frp'),
    [double]$MaxErrorRate = 0.20,
    [ValidateSet('Fatal', 'Error', 'Warning', 'Information', 'Debug')]
    [string]$LogLevel = 'Fatal',
    # frp 的预建 work connection 数量（默认 5）。0 = 不写，用 frp 默认值。
    # 用于最终报告的"frp 调平"对照组：高并发下让 frpc 提前备好连接，避免请求时现拨。
    [int]$FrpPoolCount = 0,
    [switch]$SkipPublish)

$ErrorActionPreference = 'Stop'

# -LevelsCsv 用逗号分隔字符串传：PowerShell 的 -File 模式会把 "5000,10000" 当成带千位分隔符的数字
$Levels = @($LevelsCsv -split '[,\s;]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
if ($Levels.Count -eq 0) { throw '-LevelsCsv 为空' }

$expDir = $PSScriptRoot
$repoRoot = Split-Path (Split-Path $expDir -Parent) -Parent
$outDir = Join-Path $repoRoot 'bench\out\exp'
$resultsDir = Join-Path $outDir 'results'
$runDir = Join-Path $outDir 'run'
$frpDir = Join-Path $outDir 'frp\frp_0.72.0_windows_amd64'
$runner = Join-Path $repoRoot 'bench\LoadRunner\bin\Release\net10.0\FastTunnel.LoadRunner.exe'

$ftServerPort = 17270
$frpBindPort = 17000
$frpHttpPort = 17080
$ftHost = 'bench.ftbench.net'
$frpHost = 'bench.frpbench.net'
$ftDomain = 'ftbench.net'
$ftSub = 'bench'

New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
New-Item -ItemType Directory -Path $runDir -Force | Out-Null

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor DarkGray }

# 配置文件必须是无 BOM 的 UTF-8：frp 的 TOML 解析器会把 BOM 当成非法字符
function Write-Utf8NoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
}

function Show-LogTail([string]$path, [int]$lines = 12) {
    if (-not (Test-Path $path)) { return }
    Write-Host "    --- $path ---" -ForegroundColor DarkYellow
    Get-Content $path -Tail $lines -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
}

# ---------------- 0. 源站 ----------------
$envFile = Join-Path $outDir 'env.json'
$originProcess = $null

if (Test-Path $envFile) {
    $envInfo = Get-Content $envFile -Raw | ConvertFrom-Json
    if ($OriginPort -eq 0) { $OriginPort = [int]$envInfo.OriginPort }
    if (-not $OriginPath) { $OriginPath = [string]$envInfo.OriginPath }
    Write-Step "源站来自 env.json：http://${OriginHost}:$OriginPort$OriginPath（mode=$($envInfo.Mode)）"
}

if ($OriginPort -eq 0 -and -not (Test-Path $envFile)) {
    # 用户可能手动配好了 IIS（没跑 setup 脚本）：先探测 8080 上是不是已经部署了我们的页面
    foreach ($candidate in @('/index.html', '/bench/index.html')) {
        $body = (& curl.exe -s --noproxy '*' --max-time 3 "http://127.0.0.1:8080$candidate" 2>$null | Out-String)
        if ($body -match 'benchmark target') {
            $OriginPort = 8080
            $OriginPath = $candidate
            Write-Step "探测到本机 8080 已部署源站页面：http://127.0.0.1:8080$candidate（按 IIS 源站计）"
            break
        }
    }
}

if ($OriginPort -eq 0) {
    $OriginPort = 18080
    $OriginPath = '/index.html'
    $originProcess = Start-Process -FilePath $runner `
        -ArgumentList @('serve', '--port', "$OriginPort") `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runDir 'origin.log') `
        -RedirectStandardError (Join-Path $runDir 'origin.err')
    Start-Sleep -Milliseconds 800
    Write-Step "未找到 env.json，使用内置源站 127.0.0.1:$OriginPort$OriginPath（仅用于链路验证）"
}

if (-not $OriginPath) { $OriginPath = '/index.html' }

# ---------------- 1. 准备二进制 ----------------
if (-not $SkipPublish) {
    Write-Step '发布 FastTunnel Release / 构建压测工具'
    dotnet publish (Join-Path $repoRoot 'FastTunnel.Server\FastTunnel.Server.csproj') -c Release `
        -o (Join-Path $outDir 'ft-server') --nologo -v q -p:GeneratePackageOnBuild=false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'FastTunnel.Server 发布失败' }

    dotnet publish (Join-Path $repoRoot 'FastTunnel.Client\FastTunnel.Client.csproj') -c Release `
        -o (Join-Path $outDir 'ft-client') --nologo -v q -p:GeneratePackageOnBuild=false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'FastTunnel.Client 发布失败' }

    dotnet build (Join-Path $repoRoot 'bench\LoadRunner\LoadRunner.csproj') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'LoadRunner 构建失败' }
}

if (-not (Test-Path (Join-Path $frpDir 'frps.exe'))) {
    Write-Step '下载 frp v0.72.0'
    $zip = Join-Path $outDir 'download\frp_0.72.0_windows_amd64.zip'
    New-Item -ItemType Directory -Path (Split-Path $zip -Parent) -Force | Out-Null
    Invoke-WebRequest -Uri 'https://github.com/fatedier/frp/releases/download/v0.72.0/frp_0.72.0_windows_amd64.zip' `
        -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $outDir 'frp') -Force
}

# ---------------- 2. 生成配置 ----------------
function New-FastTunnelConfigs {
    param([string]$ServerDir, [string]$ClientDir)

    New-Item -ItemType Directory -Path (Join-Path $ServerDir 'config') -Force | Out-Null
    New-Item -ItemType Directory -Path $ClientDir -Force | Out-Null

    # 日志降到 $LogLevel：正常压测期间不产生任何日志格式化/落盘
    $serverConfig = [ordered]@{
        Serilog    = @{ MinimumLevel = @{ Default = $LogLevel; Override = @{ Microsoft = 'Warning' } }; WriteTo = @() }
        FastTunnel = [ordered]@{
            BinPort              = $ftServerPort
            WebDomain            = $ftDomain
            EnableForward        = $false
            EnableMux            = $true
            EnforceSingleSession = $false
            Tls                  = @{ Enabled = $false; AutoGenerate = $false }
            Tokens               = @('BENCH_TOKEN')
            Bandwidth            = @{ DefaultBytesPerSecond = 0; BurstSeconds = 1; PerProxy = @{}; PerClient = @{} }
            Ops                  = @{ PublicEndpoints = $false }
            Abuse                = @{
                Ban   = @{ StateFile = (Join-Path $runDir 'bans.json') }
                Audit = @{ FilePath = (Join-Path $runDir 'audit.jsonl'); MemoryCapacity = 100 }
            }
        }
    }

    Write-Utf8NoBom (Join-Path $ServerDir 'config\appsettings.json') ($serverConfig | ConvertTo-Json -Depth 10)

    $clientConfig = [ordered]@{
        Serilog    = @{ MinimumLevel = @{ Default = $LogLevel; Override = @{ Microsoft = 'Warning' } }; WriteTo = @() }
        FastTunnel = [ordered]@{
            Server       = [ordered]@{
                ServerAddr                = '127.0.0.1'
                ServerPort                = $ftServerPort
                Protocol                  = 'ws'   # 明文控制通道：与 frp 关闭 TLS 对齐
                AllowUntrustedCertificate = $true
            }
            Token        = 'BENCH_TOKEN'
            EnableMux    = $true
            MuxTransport = 'tcp'
            Webs         = @(@{ LocalIp = '127.0.0.1'; LocalPort = $OriginPort; SubDomain = $ftSub })
            Forwards     = @()
        }
    }

    Write-Utf8NoBom (Join-Path $ClientDir 'appsettings.json') ($clientConfig | ConvertTo-Json -Depth 10)
}

function New-FrpConfigs {
    param([string]$Dir)

    New-Item -ItemType Directory -Path $Dir -Force | Out-Null

    $frpPoolLine = if ($FrpPoolCount -gt 0) { "transport.poolCount = $FrpPoolCount" } else { '' }

    $frps = @"
bindPort = $frpBindPort
vhostHTTPPort = $frpHttpPort

log.to = "disable"
log.level = "error"

transport.tls.force = false
"@

    $frpc = @"
serverAddr = "127.0.0.1"
serverPort = $frpBindPort

log.to = "disable"
log.level = "error"

transport.tls.enable = false
$frpPoolLine

[[proxies]]
name = "bench-http"
type = "http"
localIP = "127.0.0.1"
localPort = $OriginPort
customDomains = ["$frpHost"]
"@

    Write-Utf8NoBom (Join-Path $Dir 'frps.toml') $frps
    Write-Utf8NoBom (Join-Path $Dir 'frpc.toml') $frpc
}

New-FastTunnelConfigs -ServerDir (Join-Path $runDir 'ft-server') -ClientDir (Join-Path $runDir 'ft-client')
New-FrpConfigs -Dir (Join-Path $runDir 'frp')

# ---------------- 3. 工具函数 ----------------
function Assert-PortFree([int]$port, [string]$label) {
    $busy = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    if ($busy) {
        $ownerId = ($busy | Select-Object -First 1).OwningProcess
        $name = (Get-Process -Id $ownerId -ErrorAction SilentlyContinue).ProcessName
        throw "端口 $port（$label）已被进程 $name($ownerId) 占用：请先停止它，否则本次压测会打到旧实例上。"
    }
}

function Assert-ProcessAlive([System.Diagnostics.Process]$proc, [string]$label, [string]$logPath) {
    Start-Sleep -Milliseconds 700
    if ($proc.HasExited) {
        Write-Host "    [失败] $label 启动后立即退出（退出码 $($proc.ExitCode)）" -ForegroundColor Red
        Show-LogTail $logPath 20
        throw "$label 启动失败"
    }
}

function Wait-HttpOk {
    param([string]$Url, [string]$HostHeader, [double]$TimeoutSeconds = 60)

    # 用 curl 而不是 Invoke-WebRequest：后者会走系统代理/受限头处理，
    # 对「指定 Host 头访问 127.0.0.1」这种探测并不可靠（曾导致把代理的 404 当成隧道未就绪）。
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $code = ''

    while ((Get-Date) -lt $deadline) {
        $code = (& curl.exe -s -o NUL -w '%{http_code}' --noproxy '*' --max-time 5 `
                -H "Host: $HostHeader" $Url 2>$null | Out-String).Trim()
        if ($code -eq '200') { return $true }
        Start-Sleep -Milliseconds 400
    }

    Write-Host "    最后一次探测：HTTP '$code'  URL=$Url  Host=$HostHeader" -ForegroundColor Yellow
    return $false
}

function Run-Level {
    param(
        [string]$Framework,
        [int]$Rps,
        [int]$Conns,
        [int]$Port,
        [string]$HostHeader,
        [System.Diagnostics.Process]$ServerProc,
        [System.Diagnostics.Process]$ClientProc
    )

    $outFile = Join-Path $resultsDir ("{0}-rps{1}.json" -f $Framework, $Rps)
    if (Test-Path $outFile) { Remove-Item $outFile -Force }

    $serverCpuBefore = $null
    $clientCpuBefore = $null
    if ($ServerProc) { $ServerProc.Refresh(); $serverCpuBefore = $ServerProc.TotalProcessorTime }
    if ($ClientProc) { $ClientProc.Refresh(); $clientCpuBefore = $ClientProc.TotalProcessorTime }

    # 装了 hosts（本地 DNS）就用主机名解析；没装就退化成「连 127.0.0.1 + 指定 Host 头」，
    # 对虚拟主机路由来说两者等价，报告里会标注这次用的是哪种。
    $endpoint = @('--host', $HostHeader, '--port', "$Port")
    try {
        [System.Net.Dns]::GetHostAddresses($HostHeader) | Out-Null
    }
    catch {
        $endpoint = @('--connect', "127.0.0.1:$Port", '--host', $HostHeader)
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $exit = -1

    try {
        & $runner @endpoint --path $OriginPath `
            --rps $Rps --connections $Conns --warmup $Warmup --duration $Duration `
            --out $outFile --label "$Framework-$Rps" | Out-Null
        $exit = $LASTEXITCODE
    }
    catch {
        Write-Host "    [警告] 压测进程异常：$($_.Exception.Message)" -ForegroundColor Yellow
    }

    $sw.Stop()

    if (-not (Test-Path $outFile)) {
        # 压测工具没产出结果（参数错误 / 进程崩溃）：记一条失败行继续，不要中断整轮实验
        return [pscustomobject]@{
            Label           = "$Framework-$Rps"
            TargetRps       = $Rps
            AchievedRps     = 0
            ErrorRate       = 1
            LateSendRatio   = 1
            ThroughputMiBps = 0
            Requests        = 0
            Errors          = 0
            LatencyMs       = [pscustomobject]@{ Mean = 0; P50 = 0; P90 = 0; P99 = 0; P999 = 0; Max = 0 }
            ServerCores     = $null
            ClientCores     = $null
            ExitCode        = $exit
        }
    }

    $result = Get-Content $outFile -Raw | ConvertFrom-Json

    $serverCores = $null
    $clientCores = $null
    if ($ServerProc) {
        $ServerProc.Refresh()
        $serverCores = [math]::Round(($ServerProc.TotalProcessorTime - $serverCpuBefore).TotalSeconds / $sw.Elapsed.TotalSeconds, 2)
    }

    if ($ClientProc) {
        $ClientProc.Refresh()
        $clientCores = [math]::Round(($ClientProc.TotalProcessorTime - $clientCpuBefore).TotalSeconds / $sw.Elapsed.TotalSeconds, 2)
    }

    $result | Add-Member -NotePropertyName ServerCores -NotePropertyValue $serverCores -Force
    $result | Add-Member -NotePropertyName ClientCores -NotePropertyValue $clientCores -Force

    # 内存：私有字节（进程真实占用）+ 峰值工作集（瞬时水位）。
    # 取该级结束时的稳态值，两侧口径一致。
    $serverMem = $null
    $clientMem = $null
    $serverPeak = $null
    $clientPeak = $null

    if ($ServerProc) {
        $ServerProc.Refresh()
        $serverMem = [math]::Round($ServerProc.PrivateMemorySize64 / 1MB, 1)
        $serverPeak = [math]::Round($ServerProc.PeakWorkingSet64 / 1MB, 1)
    }

    if ($ClientProc) {
        $ClientProc.Refresh()
        $clientMem = [math]::Round($ClientProc.PrivateMemorySize64 / 1MB, 1)
        $clientPeak = [math]::Round($ClientProc.PeakWorkingSet64 / 1MB, 1)
    }

    $result | Add-Member -NotePropertyName ServerMemMB -NotePropertyValue $serverMem -Force
    $result | Add-Member -NotePropertyName ClientMemMB -NotePropertyValue $clientMem -Force
    $result | Add-Member -NotePropertyName TotalMemMB -NotePropertyValue $(
        if ($null -ne $serverMem -and $null -ne $clientMem) { [math]::Round($serverMem + $clientMem, 1) } else { $null }) -Force
    $result | Add-Member -NotePropertyName ServerPeakMB -NotePropertyValue $serverPeak -Force
    $result | Add-Member -NotePropertyName ClientPeakMB -NotePropertyValue $clientPeak -Force
    $result | Add-Member -NotePropertyName ExitCode -NotePropertyValue $exit -Force
    $result | ConvertTo-Json -Depth 6 | Set-Content -Path $outFile -Encoding utf8

    return $result
}

# ---------------- 4. 逐个框架跑阶梯 ----------------
$all = @()

try {
    foreach ($framework in $Frameworks) {
        Write-Step "===== 被测对象：$framework ====="

        $serverProc = $null
        $clientProc = $null

        if ($framework -eq 'ft') {
            $serverExe = Join-Path $outDir 'ft-server\FastTunnel.Server.exe'
            $clientExe = Join-Path $outDir 'ft-client\FastTunnel.Client.exe'

            Assert-PortFree $ftServerPort 'FastTunnel 服务端'
            $serverProc = Start-Process -FilePath $serverExe -WorkingDirectory (Join-Path $runDir 'ft-server') -PassThru `
                -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir 'ft-server.log') -RedirectStandardError (Join-Path $runDir 'ft-server.err')
            Assert-ProcessAlive $serverProc 'FastTunnel 服务端' (Join-Path $runDir 'ft-server.err')
            Start-Sleep -Milliseconds 800
            $clientProc = Start-Process -FilePath $clientExe -WorkingDirectory (Join-Path $runDir 'ft-client') -PassThru `
                -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir 'ft-client.log') -RedirectStandardError (Join-Path $runDir 'ft-client.err')
            Assert-ProcessAlive $clientProc 'FastTunnel 客户端' (Join-Path $runDir 'ft-client.err')

            $entryPort = $ftServerPort
            $entryHost = $ftHost
        }
        elseif ($framework -eq 'frp') {
            $frpsExe = Join-Path $frpDir 'frps.exe'
            $frpcExe = Join-Path $frpDir 'frpc.exe'

            Assert-PortFree $frpBindPort 'frps bindPort'
            Assert-PortFree $frpHttpPort 'frps vhostHTTPPort'
            $serverProc = Start-Process -FilePath $frpsExe -WorkingDirectory (Join-Path $runDir 'frp') -PassThru `
                -ArgumentList @('-c', (Join-Path $runDir 'frp\frps.toml')) `
                -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir 'frps.log') -RedirectStandardError (Join-Path $runDir 'frps.err')
            Assert-ProcessAlive $serverProc 'frps' (Join-Path $runDir 'frps.err')
            Start-Sleep -Milliseconds 800
            $clientProc = Start-Process -FilePath $frpcExe -WorkingDirectory (Join-Path $runDir 'frp') -PassThru `
                -ArgumentList @('-c', (Join-Path $runDir 'frp\frpc.toml')) `
                -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir 'frpc.log') -RedirectStandardError (Join-Path $runDir 'frpc.err')
            Assert-ProcessAlive $clientProc 'frpc' (Join-Path $runDir 'frpc.err')

            $entryPort = $frpHttpPort
            $entryHost = $frpHost
        }
        else {
            Write-Host "未知框架 $framework，跳过" -ForegroundColor Yellow
            continue
        }

        try {
            $ready = Wait-HttpOk -Url "http://127.0.0.1:$entryPort$OriginPath" -HostHeader $entryHost -TimeoutSeconds 60
            if (-not $ready) {
                Write-Host "    [失败] $framework 隧道 60s 内没有就绪" -ForegroundColor Red
                Show-LogTail (Join-Path $runDir 'ft-server.log')
                Show-LogTail (Join-Path $runDir 'ft-server.err')
                Show-LogTail (Join-Path $runDir 'ft-client.log')
                Show-LogTail (Join-Path $runDir 'ft-client.err')
                Show-LogTail (Join-Path $runDir 'frps.err')
                Show-LogTail (Join-Path $runDir 'frpc.err')
                continue
            }

            Write-Ok "隧道就绪：http://$entryHost`:$entryPort$OriginPath"

            foreach ($level in $Levels) {
                # -AutoConnections：连接数随档位放大，让"单连接节奏"远大于服务时间，
                # 这样测到的延迟才是"按目标速率发压时的用户延迟"，而不是排队等槽位的时间。
                $conns = if ($AutoConnections) { [math]::Min(1024, [math]::Max(64, [int]($level / 50))) } else { $Connections }

                Write-Host ("    --- {0} 目标 {1} RPS / 连接数 {2} ---" -f $framework, $level, $conns)
                $result = Run-Level -Framework $framework -Rps $level -Port $entryPort -HostHeader $entryHost `
                    -ServerProc $serverProc -ClientProc $clientProc -Conns $conns

                $all += [pscustomobject]@{
                    Framework   = $framework
                    TargetRps   = $result.TargetRps
                    Connections = $result.Connections
                    AchievedRps = $result.AchievedRps
                    ErrorRate   = $result.ErrorRate
                    LateSend    = $result.LateSendRatio
                    P50         = $result.LatencyMs.P50
                    P90         = $result.LatencyMs.P90
                    P99         = $result.LatencyMs.P99
                    P999        = $result.LatencyMs.P999
                    Max         = $result.LatencyMs.Max
                    MiBps       = $result.ThroughputMiBps
                    ServerCores = $result.ServerCores
                    ClientCores = $result.ClientCores
                    LoadCores   = $result.SelfCores
                    ServerMem   = $result.ServerMemMB
                    ClientMem   = $result.ClientMemMB
                    TotalMem    = $result.TotalMemMB
                    Requests    = $result.Requests
                    Errors      = $result.Errors
                }

                Write-Ok ("达成 {0} RPS / 错误率 {1:P2} / p50 {2}ms / p99 {3}ms / p99.9 {4}ms" -f `
                    $result.AchievedRps, $result.ErrorRate, $result.LatencyMs.P50, $result.LatencyMs.P99, $result.LatencyMs.P999)

                if ($result.ErrorRate -gt $MaxErrorRate) {
                    Write-Host ("    [中止] 错误率 {0:P1} 超过阈值 {1:P0}，该框架不再往上加压" -f $result.ErrorRate, $MaxErrorRate) -ForegroundColor Yellow
                    break
                }
            }
        }
        finally {
            foreach ($proc in @($clientProc, $serverProc)) {
                if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
            }

            Start-Sleep -Milliseconds 500
        }
    }
}
finally {
    if ($originProcess) { Stop-Process -Id $originProcess.Id -Force -ErrorAction SilentlyContinue }
}

# ---------------- 5. 汇总 ----------------
$csv = Join-Path $outDir 'results.csv'
$all | Export-Csv -Path $csv -NoTypeInformation -Encoding utf8

$report = Join-Path $outDir 'report.md'
$lines = @()
$lines += '# FastTunnel vs frp v0.72.0 本地 HTTP 穿透对照压测'
$lines += ''
$lines += "- 时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
$lines += "- 源站：http://${OriginHost}:$OriginPort$OriginPath"
$lines += "- 压测：开环定速 $($Levels -join ' / ') RPS，连接数 $Connections，预热 ${Warmup}s，测量 ${Duration}s"
$lines += '- 两个框架均为明文：fasttunnel 用 ws 控制通道 + 明文 mux；frp 设 transport.tls.enable=false'
$lines += ''
$lines += '![对比图](chart.svg)'
$lines += "- 日志：两侧都降到 $LogLevel（frp 为 log.level=error / log.to=disable）"
$lines += ''
$lines += '| 框架 | 目标 RPS | 连接数 | 达成 RPS | 错误率 | p50(ms) | p90(ms) | p99(ms) | p99.9(ms) | 最大(ms) | MiB/s | 服务端核 | 客户端核 | 压测机核 | 服务端内存(MB) | 客户端内存(MB) |'
$lines += '|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|'
foreach ($row in $all) {
    $lines += ("| {0} | {1} | {2} | {3} | {4:P2} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} |" -f `
        $row.Framework, $row.TargetRps, $row.Connections, $row.AchievedRps, $row.ErrorRate, $row.P50, $row.P90, $row.P99, $row.P999, $row.Max, $row.MiBps, $row.ServerCores, $row.ClientCores, $row.LoadCores, $row.ServerMem, $row.ClientMem)
}

$lines += ''
$lines += '## 同档位对比'
$lines += ''
$lines += '| 目标 RPS | FastTunnel 达成 | frp 达成 | FastTunnel p99 | frp p99 | FastTunnel 错误率 | frp 错误率 |'
$lines += '|---|---|---|---|---|---|---|'
foreach ($level in $Levels) {
    $ft = $all | Where-Object { $_.Framework -eq 'ft' -and $_.TargetRps -eq $level } | Select-Object -First 1
    $frp = $all | Where-Object { $_.Framework -eq 'frp' -and $_.TargetRps -eq $level } | Select-Object -First 1
    if (-not $ft -and -not $frp) { continue }

    $lines += ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f `
        $level,
        $(if ($ft) { $ft.AchievedRps } else { '-' }),
        $(if ($frp) { $frp.AchievedRps } else { '-' }),
        $(if ($ft) { $ft.P99 } else { '-' }),
        $(if ($frp) { $frp.P99 } else { '-' }),
        $(if ($ft) { '{0:P2}' -f $ft.ErrorRate } else { '-' }),
        $(if ($frp) { '{0:P2}' -f $frp.ErrorRate } else { '-' }))
}

$lines | Set-Content -Path $report -Encoding utf8

# 画对比图（失败不影响数据）
try {
    & (Join-Path $PSScriptRoot 'make-charts.ps1') -CsvPath $csv -OutPath (Join-Path $outDir 'chart.svg') | Out-Null
}
catch {
    Write-Host "    [警告] 图表生成失败：$($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ''
Write-Host '================ 结果 ================' -ForegroundColor Green
$all | Format-Table Framework, TargetRps, AchievedRps, ErrorRate, P50, P99, P999, MiBps, ServerCores, ClientCores -AutoSize
Write-Host "原始数据：$resultsDir"
Write-Host "CSV：$csv"
Write-Host "报告：$report"
