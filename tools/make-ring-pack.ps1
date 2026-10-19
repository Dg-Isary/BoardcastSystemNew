# 智能校园广播系统 · 把 third_party\ring 打成一个可上传的铃声包
# ==========================================================================
# 用途：铃声按体积/合规要求不进 git，但 CI 需要它。做法是把铃声打成一个 zip，
# 上传到你自己的"音乐库"仓库的 Release，然后让 fetch-deps.ps1 去下载。
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\tools\make-ring-pack.ps1
#   产物：dist\cbs-ring-pack.zip  + 同目录 .sha256 文本
#
# 上传步骤（一次即可）：
#   1) 在 GitHub 新建一个仓库（建议 Private，例如 cbs-music-library）；
#   2) 进入 Releases → Draft a new release → tag 填 v1 → 把 dist\cbs-ring-pack.zip 拖进去 → Publish；
#   3) 复制该资产的直链，填到 tools\fetch-deps.ps1 的 $DefaultRingUrl，
#      或在 CI 里配 secret CBS_RING_URL。
#
# 注意：本文件必须保存为 UTF-8 带 BOM（build-release.ps1 的脚本自检会检查）。

param(
    [string]$Root = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $OutDir) { $OutDir = Join-Path $Root 'dist' }

$ringDir = Join-Path $Root 'third_party\ring'
if (-not (Test-Path $ringDir)) { throw "找不到铃声目录: $ringDir" }
$files = @(Get-ChildItem $ringDir -File | Where-Object { $_.Extension -match '^\.(mp3|MP3|wav|flac)$' })
if ($files.Count -eq 0) { throw "铃声目录里没有音频文件: $ringDir" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir 'cbs-ring-pack.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
# 只把音频本身放进包里（顶层无目录），解压后直接就是 ring\*.mp3
Compress-Archive -Path ($files | ForEach-Object { $_.FullName }) -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$shaFile = $zip + '.sha256'
[System.IO.File]::WriteAllText($shaFile, ($hash + '  cbs-ring-pack.zip' + "`n"), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host ('铃声包已生成：' + $zip) -ForegroundColor Green
Write-Host ('  大小   : ' + $size + ' MB，' + $files.Count + ' 个音频')
Write-Host ('  SHA256 : ' + $hash)
Write-Host ('  校验值 : ' + $shaFile)
Write-Host ''
Write-Host '下一步：把它上传到"音乐库"仓库的 Release，然后把直链填进 tools\fetch-deps.ps1 的 $DefaultRingUrl。'