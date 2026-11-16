# 需要“以管理员身份运行”的一次性准备脚本：
#   1) 在 8080 上把源站指向 bench\exp\www（优先复用已有 8080 站点，加一个 /bench 虚拟目录；
#      8080 空闲则新建独立站点；只绑了具体主机名则给两个域名各加一条绑定）
#   2) 追加本地 DNS 记录：bench.ftbench.net / bench.frpbench.net -> 127.0.0.1
#   3) 冒烟验证源站可用，并把实际生效的源站地址写进 bench\out\exp\env.json
#
# 只做“新增”，不删除、不修改你已有的站点绑定；hosts 只动带 fasttunnel-bench 标记的行。
# 卸载：以管理员运行 teardown-iis-admin.ps1

param(
    [int]$OriginPort = 8080,
    [string]$SiteName = 'FastTunnelBench',
    [string]$VirtualDirName = 'bench',
    [string[]]$HostNames = @('bench.ftbench.net', 'bench.frpbench.net')
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Host "[失败] $message" -ForegroundColor Red
    exit 1
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail '请以管理员身份运行本脚本（右键 PowerShell -> 以管理员身份运行，再执行本文件）。'
}

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$wwwPath = Join-Path $PSScriptRoot 'www'
$envDir = Join-Path $repoRoot 'bench\out\exp'
$envFile = Join-Path $envDir 'env.json'
$hostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$marker = '# fasttunnel-bench'
$indexFile = Join-Path $wwwPath 'index.html'

if (-not (Test-Path $indexFile)) {
    Fail "找不到待部署的页面：$indexFile"
}

Write-Host "==> 源站目录：$wwwPath" -ForegroundColor Cyan
Write-Host "==> 目标端口：$OriginPort" -ForegroundColor Cyan

Import-Module WebAdministration -ErrorAction SilentlyContinue
if (-not (Get-Command Get-Website -ErrorAction SilentlyContinue)) {
    Fail '这台机器上没有可用的 IIS 管理模块（WebAdministration）。请确认已安装 IIS，或改用内置 Kestrel 源站（不生成 env.json 即可）。'
}

# ---------- 1. 部署静态页 ----------
$mode = $null
$siteUsed = $null
$originPath = '/index.html'

function Get-BindingPort([string]$binding) {
    # 形如 *:8080: / *:8080:bench.ftbench.net / [::]:8080:
    $m = [regex]::Match($binding, ':(\d+):[^:]*$')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 0
}

function Get-BindingHost([string]$binding) {
    $parts = $binding.Split(':')
    if ($parts.Length -ge 3) { return $parts[2] }
    return ''
}

$allBindings = @()
foreach ($site in Get-Website) {
    foreach ($binding in $site.Bindings.Collection) {
        $allBindings += [pscustomobject]@{
            Site     = $site.Name
            Binding  = $binding.bindingInformation
            Port     = (Get-BindingPort $binding.bindingInformation)
            HostPart = (Get-BindingHost $binding.bindingInformation)
            Physical = $site.physicalPath
        }
    }
}

$portBindings = @($allBindings | Where-Object { $_.Port -eq $OriginPort })

if ($portBindings.Count -eq 0) {
    # 端口空闲：新建独立站点，根目录就是我们的 www
    $existing = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
    if ($existing) {
        Remove-Website -Name $SiteName
    }

    try {
        New-Website -Name $SiteName -Port $OriginPort -PhysicalPath $wwwPath -Force | Out-Null
    }
    catch {
        Fail "在端口 $OriginPort 上创建 IIS 站点失败：$($_.Exception.Message)。若该端口被非 IIS 进程占用，请用 -OriginPort 指定其它端口。"
    }

    Start-Website -Name $SiteName
    $mode = 'new-site'
    $siteUsed = $SiteName
    $originPath = '/index.html'
}
else {
    $wildcard = @($portBindings | Where-Object { [string]::IsNullOrWhiteSpace($_.HostPart) })

    if ($wildcard.Count -gt 0) {
        # 已有站点占着 *:port：给它加一个虚拟目录，路径 /<vdir>/index.html
        $siteUsed = $wildcard[0].Site

        $vdirs = Get-WebVirtualDirectory -Site $siteUsed -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $VirtualDirName }
        if ($vdirs) {
            Remove-WebVirtualDirectory -Site $siteUsed -Name $VirtualDirName
        }

        try {
            New-WebVirtualDirectory -Site $siteUsed -Name $VirtualDirName -PhysicalPath $wwwPath | Out-Null
        }
        catch {
            Fail "给站点 $siteUsed 添加虚拟目录 /$VirtualDirName 失败：$($_.Exception.Message)"
        }

        $mode = 'vdir'
        $originPath = "/$VirtualDirName/index.html"
    }
    else {
        # 端口只绑了具体主机名：给我们的两个域名各加一条绑定，建独立站点
        $existing = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
        if ($existing) {
            Remove-Website -Name $SiteName
        }

        try {
            New-Website -Name $SiteName -Port $OriginPort -PhysicalPath $wwwPath -HostHeader $HostNames[0] -Force | Out-Null
            foreach ($extra in $HostNames | Select-Object -Skip 1) {
                New-WebBinding -Name $SiteName -Protocol http -Port $OriginPort -HostHeader $extra | Out-Null
            }
        }
        catch {
            Fail "在端口 $OriginPort 上创建带主机名绑定的 IIS 站点失败：$($_.Exception.Message)"
        }

        Start-Website -Name $SiteName
        $mode = 'new-site-hostheader'
        $siteUsed = $SiteName
        $originPath = '/index.html'
    }
}

Write-Host "==> IIS 部署完成（mode=$mode, site=$siteUsed, path=$originPath）" -ForegroundColor Green

# ---------- 2. 本地 DNS ----------
$lines = @()
if (Test-Path $hostsFile) {
    $lines = @(Get-Content $hostsFile -ErrorAction Stop)
}

$kept = @($lines | Where-Object { $_ -notmatch [regex]::Escape($marker) })
$entries = @(foreach ($name in $HostNames) { "127.0.0.1`t$name`t$marker" })

try {
    [System.IO.File]::WriteAllLines($hostsFile, (@($kept) + $entries), [System.Text.Encoding]::Default)
}
catch {
    Fail "写 hosts 失败：$($_.Exception.Message)"
}

Write-Host "==> hosts 已追加：$($HostNames -join ', ') -> 127.0.0.1" -ForegroundColor Green

# ---------- 3. 冒烟验证 ----------
Start-Sleep -Seconds 1

function Test-Origin([string]$url, [string]$hostHeader) {
    # 用 curl 且显式 --noproxy：Invoke-WebRequest 会走系统代理，
    # 对 127.0.0.1 的探测会被代理拦成 404，误判成“源站没部署好”。
    try {
        $code = (& curl.exe -s -o NUL -w '%{http_code}' --noproxy '*' --max-time 10 `
                -H "Host: $hostHeader" $url 2>$null | Out-String).Trim()
        return ($code -eq '200')
    }
    catch {
        Write-Host "    探测失败 ${url}（Host=$hostHeader）：$($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

$probeUrl = "http://127.0.0.1:$OriginPort$originPath"
$okDirect = Test-Origin $probeUrl $null
$okHost = $true
foreach ($name in $HostNames) {
    if (-not (Test-Origin "http://$name`:$OriginPort$originPath" $null)) { $okHost = $false }
}

if (-not $okDirect) {
    Write-Host ''
    Write-Host '源站探测失败。可能原因：' -ForegroundColor Yellow
    Write-Host '  - 8080 被别的进程占用但未由 IIS 承载；'
    Write-Host '  - 该站点禁用了静态文件或 MIME 配置异常；'
    Write-Host '  - 站点已停止。'
    Write-Host '排查：Get-Website | Format-List Name,State,PhysicalPath; Get-WebBinding | Format-Table'
    exit 1
}

# ---------- 4. 写 env.json 供压测脚本读取 ----------
New-Item -ItemType Directory -Path $envDir -Force | Out-Null
$envInfo = [ordered]@{
    Mode           = $mode
    SiteName       = $siteUsed
    VirtualDir     = $(if ($mode -eq 'vdir') { $VirtualDirName } else { $null })
    OriginPort     = $OriginPort
    OriginPath     = $originPath
    OriginUrl      = $probeUrl
    HostNames      = $HostNames
    HostHeaderMode = ($mode -eq 'new-site-hostheader')
    PreparedAt     = (Get-Date).ToString('o')
    PreparedBy     = $identity.Name
}

[System.IO.File]::WriteAllText(
    $envFile,
    ($envInfo | ConvertTo-Json -Depth 5),
    [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host '================ 准备完成 ================' -ForegroundColor Green
Write-Host "源站：$probeUrl"
$dnsState = if ($okHost) { '通过' } else { '未通过（虚拟目录模式下不影响压测）' }
Write-Host "本地 DNS：$($HostNames -join ', ') -> 127.0.0.1（直连探测：$dnsState）"
Write-Host "环境描述：$envFile"
Write-Host ''
Write-Host '接下来在普通权限下运行：'
Write-Host '  powershell -File bench\exp\run-experiment.ps1'
Write-Host ''
Write-Host '卸载（以管理员）：powershell -File bench\exp\teardown-iis-admin.ps1'
