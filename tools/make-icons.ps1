# 智能校园广播系统 · 开源版图标生成（SVG -> ICO / 安卓自适应图标）
# ==========================================================================
# 输入：third_party\Opensource.svg（无版权图标，用户自制）
# 输出：tools\opensource\icons\
#   app.ico            多尺寸 ICO（16/24/32/48/64/128/256），给 exe 图标与网页 favicon
#   favicon.svg        原样复制（浏览器优先用 svg）
#   android\mipmap-*   安卓启动图标（5 密度 × 4 文件 + anydpi XML）
#
# 【安卓安全区】自适应图标规范：108dp 画布，其中中央 72dp 可见、**中央 66dp 是安全区**
# （各厂商圆形/方形遮罩最多裁到 72dp，重要内容必须落在 66dp 内，否则会被裁掉）。
# 所以前景层把图标缩到 66/108 ≈ 61.1% 居中绘制；背景层用图标自身的浅蓝底色。
#
# 渲染方式：本机没有 ImageMagick/Inkscape，用**无头 Chrome/Edge 截图**把 SVG 光栅化。
#
# 用法：powershell -ExecutionPolicy Bypass -File .\tools\make-icons.ps1
# 注意：本文件必须保存为 UTF-8 带 BOM（build-release.ps1 的脚本自检会检查）。

param(
    [string]$Root = '',
    [string]$SvgFile = '',
    [string]$OutDir = '',
    [string]$Browser = ''
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $SvgFile) { $SvgFile = Join-Path $Root 'third_party\Opensource.svg' }
if (-not $OutDir) { $OutDir = Join-Path $Root 'tools\opensource\icons' }
if (-not (Test-Path $SvgFile)) { throw "找不到图标源文件: $SvgFile" }

# ---------- 定位浏览器（Chrome 优先，其次 Edge）----------
if (-not $Browser) {
    foreach ($c in @(
        (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe')
    )) { if (Test-Path $c) { $Browser = $c; break } }
}
if (-not $Browser -or -not (Test-Path $Browser)) { throw '找不到 Chrome/Edge，无法把 SVG 光栅化' }

Add-Type -AssemblyName System.Drawing
$work = Join-Path $Root ('run_icons\' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force -Path $work, $OutDir | Out-Null

$svgText = [System.IO.File]::ReadAllText($SvgFile, [System.Text.Encoding]::UTF8)
# 包一层 HTML：画布尺寸可控、背景透明、无滚动条
$html = Join-Path $work 'icon.html'
$pageHtml = '<!doctype html><html><head><meta charset="utf-8">' +
    '<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}svg{display:block;width:100vw;height:100vh}</style>' +
    '</head><body>' + $svgText + '</body></html>'
[System.IO.File]::WriteAllText($html, $pageHtml, (New-Object System.Text.UTF8Encoding($false)))
$url = 'file:///' + ($html -replace '\\', '/')

# ---------- 把 SVG 渲染成指定尺寸的透明 PNG ----------
# 抽查一张 PNG 的不透明像素占比（用于识别"渲染成空白"的截图）
function Get-OpaqueRatio([string]$Path) {
    try {
        $bmp = [System.Drawing.Bitmap]::FromFile($Path)
        $step = [Math]::Max(1, [int]($bmp.Width / 20))
        $total = 0; $opaque = 0
        for ($x = 0; $x -lt $bmp.Width; $x += $step) {
            for ($y = 0; $y -lt $bmp.Height; $y += $step) {
                $total++
                if ($bmp.GetPixel($x, $y).A -gt 8) { $opaque++ }
            }
        }
        $bmp.Dispose()
        if ($total -eq 0) { return 0 }
        return ($opaque / $total)
    } catch { return 0 }
}
function New-SvgPng([int]$Size, [string]$OutPng) {
    if (Test-Path $OutPng) { Remove-Item $OutPng -Force }
    # Chrome 会把 "N bytes written to ..." 打到 stderr，在 $ErrorActionPreference='Stop' 下会被当成
    # 终止错误（NativeCommandError），所以这里临时放宽，并统一吞掉输出。
    $eapSave = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $Browser --headless --disable-gpu --hide-scrollbars --no-sandbox `
        --default-background-color=00000000 --window-size="$Size,$Size" --screenshot="$OutPng" $url 2>&1 | Out-Null
    $ErrorActionPreference = $eapSave
    for ($i = 0; $i -lt 24 -and -not (Test-Path $OutPng); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path $OutPng)) { throw ("渲染失败: " + $OutPng) }
    # 【空白检测】Chrome 有时在 SVG 还没画完就截图，生成一张全透明的小文件。
    # 这里抽查不透明像素占比，太低就重试（最多 4 次），避免把空白图标打进包里。
    for ($try = 1; $try -le 4; $try++) {
        $ratio = Get-OpaqueRatio $OutPng
        if ($ratio -ge 0.15) { return $OutPng }
        Write-Host ('    [重试] ' + (Split-Path $OutPng -Leaf) + ' 只有 ' + [math]::Round($ratio * 100, 1) + '% 不透明，重新渲染（第 ' + $try + ' 次）') -ForegroundColor Yellow
        Remove-Item $OutPng -Force
        & $Browser --headless --disable-gpu --hide-scrollbars --no-sandbox `
            --default-background-color=00000000 --window-size="$Size,$Size" --screenshot="$OutPng" $url 2>&1 | Out-Null
        for ($i = 0; $i -lt 24 -and -not (Test-Path $OutPng); $i++) { Start-Sleep -Milliseconds 250 }
        Start-Sleep -Milliseconds 400
    }
    throw ("渲染结果始终是空白: " + $OutPng)
}

# ---------- 把若干 PNG 打包成 ICO（Vista+ 支持内嵌 PNG）----------
function New-Ico([string[]]$PngPaths, [string]$OutIco) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$PngPaths.Count)
    $offset = 6 + 16 * $PngPaths.Count
    $datas = @()
    foreach ($p in $PngPaths) {
        $img = [System.Drawing.Image]::FromFile($p)
        $w = $img.Width; $h = $img.Height; $img.Dispose()
        $bytes = [System.IO.File]::ReadAllBytes($p)
        $datas += , $bytes
        $wc = 0; if ($w -lt 256) { $wc = $w }
        $hc = 0; if ($h -lt 256) { $hc = $h }
        $bw.Write([byte]$wc); $bw.Write([byte]$hc)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($d in $datas) { $bw.Write($d) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($OutIco, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

Write-Host '=== 1/3 生成 ICO（渲染一张 512 母图，其余尺寸用高质量缩放）==='
# 为什么不再逐尺寸渲染：Chrome 在 128x128 上**每次**都出空白图（不是时序问题，实测 4 次重试全空），
# 而 512 母图稳定可用。用 System.Drawing 的 HighQualityBicubic 从母图缩放出各尺寸，结果更一致。
$icoMaster = New-SvgPng 512 (Join-Path $work 'ico-master.png')
$icoSizes = @(16, 24, 32, 48, 64, 128, 256)
$icoPngs = @()
$mi = [System.Drawing.Image]::FromFile($icoMaster)
foreach ($sz in $icoSizes) {
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($mi, 0, 0, $sz, $sz)
    $g.Dispose()
    $f = Join-Path $work ("ico-" + $sz + ".png")
    $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $icoPngs += $f
}
$mi.Dispose()
$appIco = Join-Path $OutDir 'app.ico'
New-Ico $icoPngs $appIco
Write-Host ('  [OK] app.ico（' + [math]::Round((Get-Item $appIco).Length / 1KB, 1) + ' KB）')
Copy-Item $SvgFile (Join-Path $OutDir 'favicon.svg') -Force
Write-Host '  [OK] favicon.svg'

Write-Host ''
Write-Host '=== 2/3 生成安卓图标（自适应 108dp，前景缩到安全区 66/108）==='
$dens = @{ 'mdpi' = 1.0; 'hdpi' = 1.5; 'xhdpi' = 2.0; 'xxhdpi' = 3.0; 'xxxhdpi' = 4.0 }
$androidOut = Join-Path $OutDir 'android'
$safeRatio = 66.0 / 108.0      # 安全区：中央 66dp / 108dp 画布

# 先渲染一张大图当素材（前景/单色/老系统整图都用它缩放）
$master = New-SvgPng 512 (Join-Path $work 'master.png')
$masterImg = [System.Drawing.Image]::FromFile($master)

# 【按包围盒居中】这个 SVG 的声波画到了 x=126（viewBox 只有 120），图形本身不对称，
# 按画布居中会整体偏右（实测偏 19px/432 ≈ 4.4%）。所以先量出非透明包围盒，再按它居中。
$mb = [System.Drawing.Bitmap]::FromFile($master)
$bx0 = $mb.Width; $bx1 = -1; $by0 = $mb.Height; $by1 = -1
for ($x = 0; $x -lt $mb.Width; $x += 2) {
    for ($y = 0; $y -lt $mb.Height; $y += 2) {
        if ($mb.GetPixel($x, $y).A -gt 8) {
            if ($x -lt $bx0) { $bx0 = $x }; if ($x -gt $bx1) { $bx1 = $x }
            if ($y -lt $by0) { $by0 = $y }; if ($y -gt $by1) { $by1 = $y }
        }
    }
}
$mb.Dispose()
$bw = [double]($bx1 - $bx0 + 1); $bh = [double]($by1 - $by0 + 1)
Write-Host ('  母图包围盒: X {0}~{1}  Y {2}~{3}（{4:N0}x{5:N0}）' -f $bx0, $bx1, $by0, $by1, $bw, $bh)

foreach ($d in $dens.Keys) {
    $dir = Join-Path $androidOut ('mipmap-' + $d)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $canvas = [int][Math]::Round(108 * $dens[$d])
    $inner = [int][Math]::Round($canvas * $safeRatio)

    # ① 前景层：透明底 + 居中缩放的图标（严格落在安全区内）
    $fg = New-Object System.Drawing.Bitmap($canvas, $canvas)
    $g = [System.Drawing.Graphics]::FromImage($fg)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.SmoothingMode = 'AntiAlias'
    # 按包围盒缩放：让**图形本身**（而不是画布）落进安全区并居中
    $sc = $inner / [Math]::Max($bw, $bh)
    $dw = $bw * $sc; $dh = $bh * $sc
    $dx = ($canvas - $dw) / 2.0; $dy = ($canvas - $dh) / 2.0
    $g.DrawImage($masterImg,
        (New-Object System.Drawing.RectangleF($dx, $dy, $dw, $dh)),
        (New-Object System.Drawing.RectangleF($bx0, $by0, $bw, $bh)),
        [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $fg.Save((Join-Path $dir 'ic_launcher_foreground.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $fg.Dispose()
    # ② 单色层（主题图标用，系统自己染色）：同前景
    Copy-Item (Join-Path $dir 'ic_launcher_foreground.png') (Join-Path $dir 'ic_launcher_monochrome.png') -Force

    # ③ 老系统整图：48dp 铺满（不需要安全区）
    $legacy = [int][Math]::Round(48 * $dens[$d])
    $lg = New-Object System.Drawing.Bitmap($legacy, $legacy)
    $g2 = [System.Drawing.Graphics]::FromImage($lg)
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.SmoothingMode = 'AntiAlias'
    $sc2 = $legacy / [Math]::Max($bw, $bh)
    $dw2 = $bw * $sc2; $dh2 = $bh * $sc2
    $g2.DrawImage($masterImg,
        (New-Object System.Drawing.RectangleF((($legacy - $dw2) / 2.0), (($legacy - $dh2) / 2.0), $dw2, $dh2)),
        (New-Object System.Drawing.RectangleF($bx0, $by0, $bw, $bh)),
        [System.Drawing.GraphicsUnit]::Pixel)
    $g2.Dispose()
    $lg.Save((Join-Path $dir 'ic_launcher.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $lg.Save((Join-Path $dir 'ic_launcher_round.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $lg.Dispose()

    Write-Host ('  [OK] mipmap-' + $d + '  画布 ' + $canvas + 'px，内容 ' + $inner + 'px（安全区 ' + [math]::Round($safeRatio * 100) + '%）')
}
$masterImg.Dispose()

# ④ 自适应图标 XML（anydpi-v26）+ 背景色
$anydpi = Join-Path $androidOut 'mipmap-anydpi-v26'
New-Item -ItemType Directory -Force -Path $anydpi | Out-Null
$xmlLines = @(
    '<?xml version="1.0" encoding="utf-8"?>',
    '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">',
    '    <background android:drawable="@color/ic_launcher_background" />',
    '    <foreground android:drawable="@mipmap/ic_launcher_foreground" />',
    '    <monochrome android:drawable="@mipmap/ic_launcher_monochrome" />',
    '</adaptive-icon>',
    ''
)
$xml = $xmlLines -join "`n"
foreach ($n in @('ic_launcher.xml', 'ic_launcher_round.xml')) {
    [System.IO.File]::WriteAllText((Join-Path $anydpi $n), $xml, (New-Object System.Text.UTF8Encoding($false)))
}
$vals = Join-Path $androidOut 'values'
New-Item -ItemType Directory -Force -Path $vals | Out-Null
$bgLines = @(
    '<?xml version="1.0" encoding="utf-8"?>',
    '<resources>',
    '    <color name="ic_launcher_background">#F0F9FF</color>',
    '</resources>',
    ''
)
[System.IO.File]::WriteAllText((Join-Path $vals 'ic_launcher_background.xml'), ($bgLines -join "`n"),
    (New-Object System.Text.UTF8Encoding($false)))
Write-Host '  [OK] 自适应 XML + 背景色 #F0F9FF'

Write-Host ''
Write-Host '=== 3/3 汇总 ==='
Get-ChildItem $OutDir -Recurse -File | ForEach-Object {
    '  {0,-46} {1,7:N1} KB' -f $_.FullName.Replace($OutDir + '\', ''), ($_.Length / 1KB)
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host ('图标已生成到 ' + $OutDir) -ForegroundColor Green
