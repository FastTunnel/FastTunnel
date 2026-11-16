# UDP end-to-end smoke: real Server + Client (UDP port forward) + local UDP echo.
# Sends small / 20KB datagrams, verifies byte-for-byte echo, under runtime-async fully on.
# Requires bench/out/server and bench/out/client published with the current config.

param(
    [int]$BinPort = 11271,
    [int]$RemotePort = 19003,
    [int]$EchoPort = 19004
)

$ErrorActionPreference = 'Stop'
foreach ($n in @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','http_proxy','https_proxy','all_proxy','no_proxy')) {
    [Environment]::SetEnvironmentVariable($n, $null, 'Process')
}

$benchDir = $PSScriptRoot
$outDir = Join-Path $benchDir 'out'
$runDir = Join-Path $outDir 'run-udp'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public static class UdpSmoke {
    public sealed class Echo : IDisposable {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _loop;
        public Echo(int port) {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            _loop = Task.Run(() => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        var data = _udp.Receive(ref remote);
                        _udp.Send(data, data.Length, remote);
                    }
                } catch { }
            });
        }
        public void Dispose() { _cts.Cancel(); _udp.Close(); try { _loop.Wait(1000); } catch {} }
    }

    public static string Probe(int port, int size, int timeoutMs) {
        using (var udp = new UdpClient()) {
            udp.Client.ReceiveTimeout = timeoutMs;
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            var payload = new byte[size];
            for (int i = 0; i < size; i++) payload[i] = (byte)(i % 251);
            udp.Send(payload, payload.Length, endpoint);
            var r = udp.Receive(ref endpoint);
            if (r.Length != size) return "FAIL: echo len " + r.Length + " != " + size;
            for (int i = 0; i < size; i++) {
                if (r[i] != (byte)(i % 251)) return "FAIL: offset " + i + " expected " + (byte)(i % 251) + " got " + r[i];
            }
            return "OK: " + size + " B byte-identical";
        }
    }
}
'@ -Language CSharp

$serverProc = $null
$clientProc = $null
$echo = $null
try {
    Write-Step "Prepare UDP run dirs (bin=$BinPort remote=$RemotePort echo=$EchoPort)"
    if (Test-Path $runDir) { Remove-Item $runDir -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $runDir 'server\config') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $runDir 'client') -Force | Out-Null

    $serverConfig = @{
        Serilog = @{ MinimumLevel = @{ Default = 'Warning' } }
        FastTunnel = @{
            BinPort = $BinPort
            WebDomain = 'bench.test.cc'
            EnableForward = $true
            AllowPorts = @()
            EnableMux = $true
            Tls = @{ Enabled = $false; AutoGenerate = $false }
            Tokens = @('BENCH_TOKEN')
            EnforceSingleSession = $false
            Ops = @{ PublicEndpoints = $true; IncludeClientLabels = $true }
            Bandwidth = @{ DefaultBytesPerSecond = 0; BurstSeconds = 1; PerProxy = @{}; PerClient = @{} }
        }
    } | ConvertTo-Json -Depth 8

    $clientConfig = @{
        Serilog = @{ MinimumLevel = @{ Default = 'Warning' } }
        FastTunnel = @{
            Server = @{
                ServerAddr = '127.0.0.1'
                ServerPort = $BinPort
                Protocol = 'ws'
                AllowUntrustedCertificate = $true
            }
            Token = 'BENCH_TOKEN'
            EnableMux = $true
            MuxTransport = 'tcp'
            Webs = @()
            Forwards = @(
                @{ LocalIp = '127.0.0.1'; LocalPort = $EchoPort; RemotePort = $RemotePort; Protocol = 'UDP' }
            )
        }
    } | ConvertTo-Json -Depth 8

    Set-Content -Path (Join-Path $runDir 'server\config\appsettings.json') -Value $serverConfig -Encoding utf8
    Set-Content -Path (Join-Path $runDir 'client\appsettings.json') -Value $clientConfig -Encoding utf8

    Write-Step 'Start local UDP echo'
    $echo = [UdpSmoke+Echo]::new($EchoPort)

    Write-Step 'Start Server / Client'
    $serverProc = Start-Process -FilePath (Join-Path $outDir 'server\FastTunnel.Server.exe') `
        -WorkingDirectory (Join-Path $runDir 'server') `
        -RedirectStandardOutput (Join-Path $runDir 'server.log') -RedirectStandardError (Join-Path $runDir 'server.err') -PassThru
    $clientProc = Start-Process -FilePath (Join-Path $outDir 'client\FastTunnel.Client.exe') `
        -WorkingDirectory (Join-Path $runDir 'client') `
        -RedirectStandardOutput (Join-Path $runDir 'client.log') -RedirectStandardError (Join-Path $runDir 'client.err') -PassThru

    Write-Step 'Wait for UDP forward ready (probe echo)'
    $deadline = (Get-Date).AddSeconds(90)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($serverProc.HasExited) { throw "Server exited early, see $runDir\server.log" }
        if ($clientProc.HasExited) { throw "Client exited early, see $runDir\client.log" }
        try {
            $res = [UdpSmoke]::Probe($RemotePort, 8, 800)
            if ($res.StartsWith('OK')) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "UDP forward port $RemotePort not ready in 90s, see $runDir logs" }

    Write-Step 'UDP forward ready. Send small / large / burst datagrams'
    Write-Host ("  small 8B    : " + [UdpSmoke]::Probe($RemotePort, 8, 3000))
    Write-Host ("  medium 1KB  : " + [UdpSmoke]::Probe($RemotePort, 1024, 3000))
    Write-Host ("  large 20KB  : " + [UdpSmoke]::Probe($RemotePort, 20480, 5000))
    $burstFail = ''
    for ($i = 0; $i -lt 20; $i++) {
        $r = [UdpSmoke]::Probe($RemotePort, 512, 3000)
        if (-not $r.StartsWith('OK')) { $burstFail = $r; break }
    }
    if ($burstFail) { Write-Host ("  burst 20x   : FAIL: " + $burstFail) } else { Write-Host '  burst 20x   : OK: all 20 datagrams byte-identical' }
    Write-Step 'UDP smoke done'
}
finally {
    if ($echo) { $echo.Dispose() }
    foreach ($proc in @($clientProc, $serverProc)) {
        if ($proc -and -not $proc.HasExited) { try { $proc.Kill($true) } catch {} }
    }
}
