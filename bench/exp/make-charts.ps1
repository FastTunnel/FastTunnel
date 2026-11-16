# 把 results.csv 画成一张对比图（三个面板：p99 延迟 / 达成速率 / 错误率）。
# 产物：bench\out\exp\chart.svg（report.md 里会以相对路径引用）
# 用法：powershell -File bench\exp\make-charts.ps1

param(
    [string]$CsvPath = (Join-Path $PSScriptRoot '..\out\exp\results.csv'),
    [string]$OutPath = (Join-Path $PSScriptRoot '..\out\exp\chart.svg')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CsvPath)) { throw "找不到 $CsvPath" }

$rows = @(Import-Csv $CsvPath)
if ($rows.Count -eq 0) { throw 'results.csv 是空的' }

$frameworks = @($rows | Select-Object -ExpandProperty Framework -Unique)
$levels = @($rows | Select-Object -ExpandProperty TargetRps -Unique | ForEach-Object { [int]$_ } | Sort-Object)
if ($levels.Count -eq 0) { throw 'results.csv 里没有档位数据' }

$colors = @{ 'ft' = '#2f7ed8'; 'frp' = '#d9534f' }
$labels = @{ 'ft' = 'FastTunnel'; 'frp' = 'frp 0.72.0' }

$width = 960
$panelHeight = 260
$paddingLeft = 78
$paddingRight = 24
$paddingTop = 34
$paddingBottom = 34
$gap = 46

$svg = New-Object System.Text.StringBuilder
function Add([string]$text) { $null = $svg.AppendLine($text) }

$totalHeight = $paddingTop + ($panelHeight + $gap) * 3
Add "<svg xmlns='http://www.w3.org/2000/svg' width='$width' height='$totalHeight' font-family='Segoe UI, sans-serif' font-size='12'>"
Add "<rect width='100%' height='100%' fill='#ffffff'/>"

# 横向像素位置：按档位序号均匀铺开
function Get-X([int]$index) {
    if ($levels.Count -le 1) { return ($paddingLeft + ($width - $paddingLeft - $paddingRight) / 2) }
    return $paddingLeft + ($width - $paddingLeft - $paddingRight) * $index / ($levels.Count - 1)
}

$panels = @(
    @{ Title = 'p99 延迟 (ms)'; Field = 'P99'; Max = $null },
    @{ Title = '达成速率 (RPS)'; Field = 'AchievedRps'; Max = $null },
    @{ Title = '错误率 (%)'; Field = 'ErrorRate'; Max = 1 }
)

$panelIndex = 0
foreach ($panel in $panels) {
    $top = $paddingTop + $panelIndex * ($panelHeight + $gap)
    $bottom = $top + $panelHeight

    # 计算该面板的最大值
    $values = @()
    foreach ($row in $rows) { $values += [double]$row.($panel.Field) }
    $max = ($values | Measure-Object -Maximum).Maximum
    if ($panel.Max) { $max = [math]::Max($max, [double]$panel.Max) }
    if ($max -le 0) { $max = 1 }

    Add "<text x='$paddingLeft' y='$($top - 12)' font-size='15' font-weight='600'>$($panel.Title)</text>"

    # 坐标轴 + 网格
    for ($g = 0; $g -le 4; $g++) {
        $y = $bottom - $panelHeight * $g / 4
        Add "<line x1='$paddingLeft' y1='$y' x2='$($width - $paddingRight)' y2='$y' stroke='#e6e6e6'/>"
        $labelValue = $max * $g / 4
        $text = if ($panel.Field -eq 'ErrorRate') { '{0:P0}' -f $labelValue } else { '{0:N0}' -f $labelValue }
        Add "<text x='$($paddingLeft - 8)' y='$($y + 4)' text-anchor='end' fill='#666'>$text</text>"
    }

    Add "<line x1='$paddingLeft' y1='$bottom' x2='$($width - $paddingRight)' y2='$bottom' stroke='#999'/>"
    Add "<line x1='$paddingLeft' y1='$top' x2='$paddingLeft' y2='$bottom' stroke='#999'/>"

    # x 轴标签
    for ($i = 0; $i -lt $levels.Count; $i++) {
        $x = Get-X $i
        Add "<text x='$x' y='$($bottom + 18)' text-anchor='middle' fill='#666'>$($levels[$i])</text>"
    }

    # 每档的目标速率参考线（仅达成速率面板）
    if ($panel.Field -eq 'AchievedRps') {
        $points = @()
        for ($i = 0; $i -lt $levels.Count; $i++) {
            $x = Get-X $i
            $y = $bottom - $panelHeight * ([math]::Min(1.0, $levels[$i] / $max))
            $points += "$x,$y"
        }
        Add "<polyline points='$($points -join ' ')' fill='none' stroke='#bbb' stroke-dasharray='5,4' stroke-width='1.5'/>"
    }

    # 每个框架一条折线（缺档处断开）
    foreach ($framework in $frameworks) {
        $segments = @()
        $current = @()
        for ($i = 0; $i -lt $levels.Count; $i++) {
            $row = $rows | Where-Object { $_.Framework -eq $framework -and [int]$_.TargetRps -eq $levels[$i] } | Select-Object -First 1
            if (-not $row) {
                if ($current.Count -gt 0) { $segments += ,@($current); $current = @() }
                continue
            }

            $value = [double]$row.($panel.Field)
            $x = Get-X $i
            $y = $bottom - $panelHeight * ([math]::Min(1.0, $value / $max))
            $current += "$x,$y"
        }

        if ($current.Count -gt 0) { $segments += ,@($current) }

        $color = if ($colors.ContainsKey($framework)) { $colors[$framework] } else { '#666' }
        foreach ($segment in $segments) {
            if ($segment.Count -eq 1) {
                $xy = $segment[0].Split(',')
                Add "<circle cx='$($xy[0])' cy='$($xy[1])' r='3' fill='$color'/>"
            }
            else {
                Add "<polyline points='$($segment -join ' ')' fill='none' stroke='$color' stroke-width='2.2'/>"
            }

            foreach ($point in $segment) {
                $xy = $point.Split(',')
                Add "<circle cx='$($xy[0])' cy='$($xy[1])' r='3' fill='$color'/>"
            }
        }
    }

    $panelIndex++
}

# 图例
$legendY = $paddingTop - 12
$legendX = $width - $paddingRight - 210

$offset = 0
foreach ($framework in $frameworks) {
    $color = if ($colors.ContainsKey($framework)) { $colors[$framework] } else { '#666' }
    $name = if ($labels.ContainsKey($framework)) { $labels[$framework] } else { $framework }
    Add "<line x1='$($legendX + $offset)' y1='$legendY' x2='$($legendX + $offset + 18)' y2='$legendY' stroke='$color' stroke-width='2.5'/>"
    Add "<text x='$($legendX + $offset + 24)' y='$($legendY + 4)' fill='#333'>$name</text>"
    $offset += 108
}

Add "<line x1='$($legendX + $offset)' y1='$legendY' x2='$($legendX + $offset + 18)' y2='$legendY' stroke='#bbb' stroke-dasharray='5,4' stroke-width='1.5'/>"
Add "<text x='$($legendX + $offset + 24)' y='$($legendY + 4)' fill='#333'>目标速率</text>"

Add "<text x='$paddingLeft' y='$(($totalHeight - 10))' fill='#888'>横轴：目标 RPS；数据来自 bench\out\exp\results.csv</text>"
Add '</svg>'

New-Item -ItemType Directory -Path (Split-Path $OutPath -Parent) -Force | Out-Null
[System.IO.File]::WriteAllText($OutPath, $svg.ToString(), (New-Object System.Text.UTF8Encoding $false))
Write-Host "chart => $OutPath"
