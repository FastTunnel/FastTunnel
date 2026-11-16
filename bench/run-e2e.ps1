# 端到端吞吐基准：真实 FastTunnel.Server + FastTunnel.Client 两个进程，
# 访客 → 服务端端口转发 → 多路复用链路 → 客户端 → 内网回显服务。
#
# 用法（在仓库根目录）：powershell -File bench/run-e2e.ps1 -TotalMiB 2048 -Connections 1
#
# 端口都可覆盖，默认值刻意避开产品默认端口（1270/3389），避免与本机正在跑的服务撞车。

param(
    [int]$BinPort = 11270,
    [int]$RemotePort = 19002,
    [int]$EchoPort = 19001,
    [int]$TotalMiB = 2048,
    [int]$ChunkKB = 64,
    [int]$Connections = 1,
    [switch]$UseTls,
    [switch]$SkipPublish,
    [string]$ClientExe,
    [string]$ServerExe
)

$ErrorActionPreference = 'Stop'

$benchDir = $PSScriptRoot
$repoRoot = Split-Path $benchDir -Parent
$outDir = Join-Path $benchDir 'out'
$runDir = Join-Path $outDir 'run'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

try {
    if (-not $SkipPublish) {
        Write-Step "发布 Server / Client / Bench（Release）"
        dotnet publish (Join-Path $repoRoot 'FastTunnel.Server\FastTunnel.Server.csproj') -c Release -o (Join-Path $outDir 'server') --nologo -v q -p:GeneratePackageOnBuild=false
        if ($LASTEXITCODE -ne 0) { throw 'server publish 失败' }

        dotnet publish (Join-Path $repoRoot 'FastTunnel.Client\FastTunnel.Client.csproj') -c Release -o (Join-Path $outDir 'client') --nologo -v q -p:GeneratePackageOnBuild=false
        if ($LASTEXITCODE -ne 0) { throw 'client publish 失败' }

        dotnet build (Join-Path $benchDir 'FastTunnel.Bench\FastTunnel.Bench.csproj') -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'bench build 失败' }
    }

    Write-Step "准备运行目录与配置（端口 bin=$BinPort remote=$RemotePort echo=$EchoPort）"
    foreach ($dir in @((Join-Path $runDir 'server'), (Join-Path $runDir 'client'))) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Path (Join-Path $dir 'config') -Force | Out-Null
    }

    # 服务端：关掉 TLS（基准只测明文数据面）、打开端口转发、单 client token
    $serverConfig = @{
        Serilog   = @{ MinimumLevel = @{ Default = 'Warning' } }
        FastTunnel = @{
            BinPort       = $BinPort
            WebDomain     = 'bench.test.cc'
            EnableForward = $true
            AllowPorts    = @()
            EnableMux     = $true
            Tls           = @{ Enabled = [bool]$UseTls; AutoGenerate = [bool]$UseTls }
            Tokens        = @('BENCH_TOKEN')
            EnforceSingleSession = $false
            Ops           = @{ PublicEndpoints = $true; IncludeClientLabels = $true }
            Bandwidth     = @{ DefaultBytesPerSecond = 0; BurstSeconds = 1; PerProxy = @{}; PerClient = @{} }
        }
    } | ConvertTo-Json -Depth 8

    $clientConfig = @{
        Serilog   = @{ MinimumLevel = @{ Default = 'Warning' } }
        FastTunnel = @{
            Server = @{
                ServerAddr   = '127.0.0.1'
                ServerPort   = $BinPort
                Protocol     = $(if ($UseTls) { 'wss' } else { 'ws' })
                AllowUntrustedCertificate = $true
            }
            Token        = 'BENCH_TOKEN'
            EnableMux    = $true
            MuxTransport = 'tcp'
            Webs         = @()
            Forwards     = @(
                @{ LocalIp = '127.0.0.1'; LocalPort = $EchoPort; RemotePort = $RemotePort; Protocol = 'TCP' }
            )
        }
    } | ConvertTo-Json -Depth 8

    Set-Content -Path (Join-Path $runDir 'server\config\appsettings.json') -Value $serverConfig -Encoding utf8
    Set-Content -Path (Join-Path $runDir 'client\appsettings.json') -Value $clientConfig -Encoding utf8

    $serverExe = if ($ServerExe) { $ServerExe } else { Join-Path $outDir 'server\FastTunnel.Server.exe' }
    $clientExe = if ($ClientExe) { $ClientExe } else { Join-Path $outDir 'client\FastTunnel.Client.exe' }
    $benchExe = Join-Path $benchDir 'FastTunnel.Bench\bin\Release\net11.0\FastTunnel.Bench.exe'
    Write-Host "server exe: $serverExe"
    Write-Host "client exe: $clientExe"

    Write-Step '启动服务端 / 客户端'
    $serverProc = Start-Process -FilePath $serverExe -WorkingDirectory (Join-Path $runDir 'server') `
        -RedirectStandardOutput (Join-Path $runDir 'server.log') -RedirectStandardError (Join-Path $runDir 'server.err') -PassThru
    $clientProc = Start-Process -FilePath $clientExe -WorkingDirectory (Join-Path $runDir 'client') `
        -RedirectStandardOutput (Join-Path $runDir 'client.log') -RedirectStandardError (Join-Path $runDir 'client.err') -PassThru

    # 等隧道就绪：转发端口能连上，说明登录 + 端口映射都完成了
    $deadline = (Get-Date).AddSeconds(90)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($serverProc.HasExited) { throw "服务端启动失败，见 $runDir\server.log" }
        if ($clientProc.HasExited) { throw "客户端启动失败，见 $runDir\client.log" }
        try {
            $probe = [System.Net.Sockets.TcpClient]::new()
            $probe.Connect('127.0.0.1', $RemotePort)
            $probe.Close()
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $ready) { throw "转发端口 $RemotePort 在 90s 内没有就绪，见 $runDir 下的日志" }
    Write-Step '隧道已就绪，开始压测'

    $serverProc.Refresh(); $clientProc.Refresh()
    $serverCpuBefore = $serverProc.TotalProcessorTime
    $clientCpuBefore = $clientProc.TotalProcessorTime

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $benchExe load $RemotePort $EchoPort $TotalMiB $ChunkKB $Connections
    $benchExit = $LASTEXITCODE
    $sw.Stop()

    $serverProc.Refresh(); $clientProc.Refresh()
    $seconds = [math]::Max(0.001, $sw.Elapsed.TotalSeconds)
    $serverCpu = ($serverProc.TotalProcessorTime - $serverCpuBefore).TotalSeconds
    $clientCpu = ($clientProc.TotalProcessorTime - $clientCpuBefore).TotalSeconds

    Write-Host ''
    Write-Host ("进程 CPU（压测全程 {0:F2}s）：server {1:F2}s = {2:F2} 核   client {3:F2}s = {4:F2} 核" -f `
        $seconds, $serverCpu, ($serverCpu / $seconds), $clientCpu, ($clientCpu / $seconds))

    if ($benchExit -ne 0) { throw "压测进程失败，exit=$benchExit" }
}
finally {
    foreach ($proc in @($serverProc, $clientProc)) {
        if ($proc -and -not $proc.HasExited) {
            try { $proc.Kill($true) } catch { }
        }
    }
}
