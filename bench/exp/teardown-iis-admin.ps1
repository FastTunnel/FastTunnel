# 卸载 setup-iis-admin.ps1 的改动：删掉自己加的站点 / 虚拟目录 / hosts 记录。
# 只动标记为自己的东西，不碰你原有站点与 hosts 其他行。需要管理员权限。

param(
    [int]$OriginPort = 8080,
    [string]$SiteName = 'FastTunnelBench',
    [string]$VirtualDirName = 'bench'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '[失败] 请以管理员身份运行。' -ForegroundColor Red
    exit 1
}

Import-Module WebAdministration -ErrorAction Stop

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$envFile = Join-Path $repoRoot 'bench\out\exp\env.json'

if (Test-Path $envFile) {
    $env = Get-Content $envFile -Raw | ConvertFrom-Json
    if ($env.SiteName) { $SiteName = $env.SiteName }
    if ($env.VirtualDir) { $VirtualDirName = $env.VirtualDir }
    if ($env.OriginPort) { $OriginPort = $env.OriginPort }
    Write-Host "==> 读取到上次部署信息：site=$SiteName vdir=$VirtualDirName port=$OriginPort"
}

# 1. 删掉自己建的站点
$site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($site -and $site.PhysicalPath -like '*bench\exp\www*') {
    Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
    Remove-Website -Name $SiteName
    Write-Host "==> 已删除站点 $SiteName" -ForegroundColor Green
}

# 2. 删掉自己加的虚拟目录（只删指向本仓库 www 的那个）
foreach ($siteItem in Get-Website) {
    $vdir = Get-WebVirtualDirectory -Site $siteItem.Name -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $VirtualDirName -and $_.PhysicalPath -like '*bench\exp\www*' }

    if ($vdir) {
        Remove-WebVirtualDirectory -Site $siteItem.Name -Name $VirtualDirName
        Write-Host "==> 已删除虚拟目录 $($siteItem.Name)/$VirtualDirName" -ForegroundColor Green
    }
}

# 3. 清理 hosts 标记行
$hostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$lines = [System.IO.File]::ReadAllLines($hostsFile)
$kept = @($lines | Where-Object { $_ -notmatch '# fasttunnel-bench' })
if ($kept.Count -ne $lines.Count) {
    [System.IO.File]::WriteAllText($hostsFile, ($kept -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
    Write-Host '==> 已清理 hosts 中的 fasttunnel-bench 记录' -ForegroundColor Green
}

Write-Host '完成。' -ForegroundColor Green
