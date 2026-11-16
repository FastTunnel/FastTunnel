# 起停隧道并跑一组压测（1 条 / N 条并发流），用于改动前后对比。
# 前置：先跑过 bench/run-e2e.ps1 -SkipPublish 一次，或用 -Publish 重新发布。

param(
    [int]$BinPort = 11270,
    [int]$RemotePort = 19002,
    [int]$EchoPort = 19001,
    [int]$TotalMiB = 512,
    [int]$ChunkKB = 64,
    [int[]]$Connections = @(1, 8),
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$benchDir = $PSScriptRoot
$repoRoot = Split-Path $benchDir -Parent
$outDir = Join-Path $benchDir 'out'
$runDir = Join-Path $outDir 'run'

if ($Publish) {
    dotnet publish (Join-Path $repoRoot 'FastTunnel.Server\FastTunnel.Server.csproj') -c Release -o (Join-Path $outDir 'server') --nologo -v q -p:GeneratePackageOnBuild=false
    dotnet publish (Join-Path $repoRoot 'FastTunnel.Client\FastTunnel.Client.csproj') -c Release -o (Join-Path $outDir 'client') --nologo -v q -p:GeneratePackageOnBuild=false
    dotnet build (Join-Path $benchDir 'FastTunnel.Bench\FastTunnel.Bench.csproj') -c Release --nologo -v q
}

$benchExe = Join-Path $benchDir 'FastTunnel.Bench\bin\Release\net10.0\FastTunnel.Bench.exe'
$serverProc = $null
$clientProc = $null

try {
    $serverProc = Start-Process -FilePath (Join-Path $outDir 'server\FastTunnel.Server.exe') `
        -WorkingDirectory (Join-Path $runDir 'server') `
        -RedirectStandardOutput (Join-Path $runDir 'server.log') -RedirectStandardError (Join-Path $runDir 'server.err') -PassThru
    $clientProc = Start-Process -FilePath (Join-Path $outDir 'client\FastTunnel.Client.exe') `
        -WorkingDirectory (Join-Path $runDir 'client') `
        -RedirectStandardOutput (Join-Path $runDir 'client.log') -RedirectStandardError (Join-Path $runDir 'client.err') -PassThru

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try {
            $probe = [System.Net.Sockets.TcpClient]::new()
            $probe.Connect('127.0.0.1', $RemotePort)
            $probe.Close()
            break
        } catch {
            Start-Sleep -Milliseconds 400
        }
    }

    foreach ($n in $Connections) {
        Write-Host "=== 并发流 $n ===" -ForegroundColor Cyan
        $serverCpuBefore = $serverProc.TotalProcessorTime
        $clientCpuBefore = $clientProc.TotalProcessorTime
        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        & $benchExe load $RemotePort $EchoPort $TotalMiB $ChunkKB $n

        $sw.Stop()
        $serverProc.Refresh(); $clientProc.Refresh()
        $seconds = [math]::Max(0.001, $sw.Elapsed.TotalSeconds)
        Write-Host ("进程 CPU：server {0:F2} 核   client {1:F2} 核" -f `
            (($serverProc.TotalProcessorTime - $serverCpuBefore).TotalSeconds / $seconds), `
            (($clientProc.TotalProcessorTime - $clientCpuBefore).TotalSeconds / $seconds))
    }
}
finally {
    foreach ($proc in @($serverProc, $clientProc)) {
        if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
}
