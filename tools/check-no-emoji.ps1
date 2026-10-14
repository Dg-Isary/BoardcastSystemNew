# 门禁：源码里不允许出现 Emoji / 会被渲染成 Emoji 的符号字符
# 背景：界面一律用 Font Awesome 图标，禁用任何 Emoji（用户多次强调、屡次反弹）。
# 注意：本文件必须保存为 **UTF-8 with BOM**，否则 Windows PowerShell 5.1 会按 GBK 读，
#       中文注释会被截断成语法错误（踩过：用编辑器改写本文件时丢了 BOM）。
# 常见误用（这里只写码点，避免门禁拦到自己）：
#   U+26A0 警告三角、U+2714 粗对勾、U+2716 粗叉、U+2757 感叹、U+2705 白对勾、
#   U+2605 星、U+26A1 闪电 等 —— Windows 会用 emoji 字体渲染成彩色图形，与界面风格冲突。
#
# 说明：U+2190~U+21FF 的排版箭头（向右箭头/双线箭头/上箭头/左右箭头）**不算** Emoji，
#       是本项目既有的排版风格，不在拦截范围。
param(
  [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

# BMP 里的 Emoji/符号区（含变体选择符 U+FE0F 与零宽连接符 U+200D）
$reBmp = [regex]'[\u2600-\u27BF\u2B00-\u2BFF\uFE0F\u24B6-\u24FF\u200D]'
# 星平面（U+10000 以上，Emoji 主体都在这里，用代理对匹配）
$reAstral = [regex]'[\uD800-\uDBFF][\uDC00-\uDFFF]'

# 扫描范围：电脑端源码 + 项目脚本 + 安卓端源码（.kt/.xml/.bat 同样禁止 Emoji 与 U+2605 这类符号）
$exts = @('*.cs', '*.html', '*.js', '*.css', '*.md', '*.ps1', '*.py', '*.iss', '*.json', '*.jsonl', '*.kt', '*.kts', '*.xml', '*.bat')
$files = @()
foreach ($d in @((Join-Path $Root 'BroadcastSystemNet'), (Join-Path $Root 'tools'), (Join-Path $Root 'BroadcastApp'))) {
  if (Test-Path $d) {
    $files += Get-ChildItem $d -Recurse -File -Include $exts |
      Where-Object { $_.FullName -notmatch '\\(bin|obj|\.build[^\\]*|old|logs|publish|node_modules|build|\.gradle[^\\]*|\.tools|tools|run_alt|\.out)\\' }
  }
}
foreach ($n in @('README.md', '人工测试清单.md')) {
  $p = Join-Path $Root $n
  if (Test-Path $p) { $files += Get-Item $p }
}

$bad = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
  $no = 0
  foreach ($line in [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)) {
    $no++
    $m = $reBmp.Match($line)
    $astral = $false
    if (-not $m.Success) { $m = $reAstral.Match($line); $astral = $m.Success }
    if (-not $m.Success) { continue }
    $cp = 0
    if ($astral) { $cp = [char]::ConvertToUtf32($m.Value[0], $m.Value[1]) } else { $cp = [int]$m.Value[0] }
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
    $snippet = $line.Trim()
    if ($snippet.Length -gt 70) { $snippet = $snippet.Substring(0, 70) }
    $bad.Add(("  {0}:{1}  U+{2:X4}  {3}" -f $rel, $no, $cp, $snippet))
  }
}

$bad = $bad | Select-Object -Unique
if ($bad.Count -gt 0) {
  Write-Host "发现 Emoji 字符 $($bad.Count) 处（界面禁止 Emoji，请改用 Font Awesome 图标或纯文字）：" -ForegroundColor Red
  $bad | ForEach-Object { Write-Host $_ }
  exit 1
}
Write-Host ("EMOJI_CHECK 通过：扫描 " + $files.Count + " 个文件，没有 Emoji 字符。") -ForegroundColor Green
exit 0
