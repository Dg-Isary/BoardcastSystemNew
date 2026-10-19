# 智能校园广播系统 · 自动获取构建依赖（ffmpeg + 铃声包）
# ==========================================================================
# 为什么要它：ffmpeg（约 200MB）与铃声（约 31MB）按体积/合规要求**不进 git**，
# 但 CI（GitHub Actions）与全新克隆都需要它们才能打出可用的安装包。
# 本脚本把"手工准备"变成一条命令。
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\tools\fetch-deps.ps1
#   ... -SkipRing                只取 ffmpeg
#   ... -SkipFfmpeg              只取铃声
#   ... -RingUrl <zip 直链>      铃声包地址（默认见下）
#   ... -Proxy http://host:port  本机无外网时走代理
#   ... -Force                   已存在也重新下载
#
# ffmpeg 来源：BtbN/FFmpeg-Builds 的 **LGPL** win64 非 shared 版
#   · 必须 LGPL：GPL 版会要求整个分发遵循 GPL，与本项目的 MIT 不兼容；
#   · 必须非 shared：静态链接，只要 ffmpeg/ffplay/ffprobe 三个 exe，不依赖 DLL；
#   · 版本号自动取"最新"（资产名会随上游升级变化，所以查 API 而不是写死文件名）。
#
# 注意：本文件必须保存为 UTF-8 带 BOM（build-release.ps1 的脚本自检会检查）。

param(
    [string]$Root = '',
    [switch]$SkipFfmpeg,
    [switch]$SkipRing,
    [string]$RingUrl = '',
    # 铃声包在**私有**仓库时需要的令牌（只读即可）。也可用环境变量 CBS_ASSET_TOKEN。
    [string]$RingToken = '',
    [string]$Proxy = '',
    [switch]$Force,
    # 铃声必须下载成功（默认失败只警告，构建继续）
    [switch]$RequireRing
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path $Root)) { throw "找不到仓库根目录: $Root" }

# 铃声包默认地址：填你"音乐库"仓库的 Release 资产直链（见 README）。
# 也可以每次用 -RingUrl 传，或在 CI 里用 secret CBS_RING_URL 覆盖。
$DefaultRingUrl = 'https://github.com/Dg-Isary/cbs-music-library/releases/download/v1/cbs-ring-pack.zip'

$ffDir   = Join-Path $Root 'third_party\ffmpeg'
$ringDir = Join-Path $Root 'third_party\ring'
$tmp     = Join-Path ([System.IO.Path]::GetTempPath()) ('cbs-deps-' + [guid]::NewGuid().ToString('N').Substring(0,8))

function Get-WebFile([string]$Url, [string]$OutFile) {
    $p = @{ Uri = $Url; OutFile = $OutFile; UseBasicParsing = $true; TimeoutSec = 1800 }
    if ($Proxy) { $p['Proxy'] = $Proxy }
    # 私有仓库的 Release 资产需要鉴权：带 Authorization 请求后 GitHub 会 302 到带签名的地址，
    # 那个地址本身自带授权，所以只需要在第一跳带上令牌即可。
    if ($RingToken) { $p['Headers'] = @{ Authorization = ('Bearer ' + $RingToken) } }
    $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'   # 进度条会让下载慢十倍
    try { Invoke-WebRequest @p | Out-Null } finally { $ProgressPreference = $old }
}

# GitHub **私有**仓库的 Release 资产：用 API 端点下载。
# 为什么不能直接用 releases/download/... ：那个地址会 302 到 objects.githubusercontent.com，
# 跨域重定向时 Authorization 头会被丢掉 -> 404（CI 上实测过）。
# API 端点 /repos/{o}/{r}/releases/assets/{id} + Accept: application/octet-stream 直接回字节，不重定向。
function Get-GitHubReleaseAsset([string]$Url, [string]$Token, [string]$OutFile) {
    if (-not $Token) { return $false }
    if ($Url -notmatch '^https://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/(.+)$') { return $false }
    $owner = $Matches[1]; $repo = $Matches[2]; $tag = $Matches[3]
    $name = [uri]::UnescapeDataString($Matches[4])
    $hdr = @{ Authorization = ('Bearer ' + $Token); 'User-Agent' = 'cbs-build'; 'Accept' = 'application/vnd.github+json' }
    $p1 = @{ Uri = ('https://api.github.com/repos/' + $owner + '/' + $repo + '/releases/tags/' + [uri]::EscapeDataString($tag)); Headers = $hdr; TimeoutSec = 120 }
    if ($Proxy) { $p1['Proxy'] = $Proxy }
    $rel = Invoke-RestMethod @p1
    $asset = $rel.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) { throw ('release ' + $tag + ' 里没有资产 ' + $name) }
    $hdr2 = @{ Authorization = ('Bearer ' + $Token); 'User-Agent' = 'cbs-build'; 'Accept' = 'application/octet-stream' }
    $p2 = @{ Uri = ('https://api.github.com/repos/' + $owner + '/' + $repo + '/releases/assets/' + $asset.id); Headers = $hdr2; OutFile = $OutFile; UseBasicParsing = $true; TimeoutSec = 1800 }
    if ($Proxy) { $p2['Proxy'] = $Proxy }
    $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest @p2 | Out-Null } finally { $ProgressPreference = $old }
    return $true
}
function Get-Json([string]$Url) {
    $p = @{ Uri = $Url; TimeoutSec = 120; Headers = @{ 'User-Agent' = 'cbs-build' } }
    if ($Proxy) { $p['Proxy'] = $Proxy }
    return Invoke-RestMethod @p
}

if (-not $RingToken) { $RingToken = $env:CBS_ASSET_TOKEN }
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    # ---------------- ffmpeg ----------------
    if (-not $SkipFfmpeg) {
        $need = $Force -or -not (Test-Path (Join-Path $ffDir 'ffplay.exe'))
        if (-not $need) {
            Write-Host '[跳过] third_party\ffmpeg 已存在（要重下请加 -Force）'
        } else {
            Write-Host '=== 获取 ffmpeg（BtbN LGPL win64） ==='
            $rel = Get-Json 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
            # 只认 lgpl + win64，且排除 -shared（我们要静态链接的三个 exe）
            $asset = $rel.assets |
                Where-Object { $_.name -match 'win64-lgpl' -and $_.name -notmatch 'shared' } |
                Sort-Object { if ($_.name -match 'n(\d+)\.(\d+)') { [int]$Matches[1] * 100 + [int]$Matches[2] } else { 0 } } -Descending |
                Select-Object -First 1
            if (-not $asset) { throw '没在 BtbN 最新发布里找到 win64-lgpl（非 shared）资产' }
            Write-Host ('  选中: ' + $asset.name + '（' + [math]::Round($asset.size / 1MB, 1) + ' MB）')
            $zip = Join-Path $tmp 'ffmpeg.zip'
            Get-WebFile $asset.browser_download_url $zip
            Write-Host '  下载完成，解压中...'
            Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp 'ff') -Force
            $bin = Get-ChildItem (Join-Path $tmp 'ff') -Recurse -Directory -Filter 'bin' | Select-Object -First 1
            if (-not $bin) { throw '压缩包里没找到 bin 目录' }
            New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
            $copied = 0
            foreach ($n in @('ffmpeg.exe', 'ffplay.exe', 'ffprobe.exe')) {
                $s = Join-Path $bin.FullName $n
                if (-not (Test-Path $s)) { throw ('压缩包里缺少 ' + $n) }
                Copy-Item $s (Join-Path $ffDir $n) -Force
                $copied++
            }
            # 非 shared 版没有 DLL；万一有（shared 误用）也一并拷走，避免运行时报缺 DLL
            Get-ChildItem $bin.FullName -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
                Copy-Item $_.FullName (Join-Path $ffDir $_.Name) -Force
            }
            Write-Host ('  [OK] 已放入 third_party\ffmpeg（' + $copied + ' 个 exe）：' + $asset.name) -ForegroundColor Green
        }
    }

    # ---------------- 铃声 ----------------
    if (-not $SkipRing) {
        $need = $Force -or -not (Test-Path $ringDir) -or (@(Get-ChildItem $ringDir -Filter '*.mp3' -ErrorAction SilentlyContinue).Count -eq 0)
        if (-not $need) {
            Write-Host ('[跳过] third_party\ring 已有 ' + @(Get-ChildItem $ringDir -Filter '*.mp3').Count + ' 个音频（要重下请加 -Force）')
        } else {
            $url = if ($RingUrl) { $RingUrl } else { $DefaultRingUrl }
            if (-not $url) {
                throw ('没有铃声包地址。请任选一种：' + "`n" +
                       '  a) 用 -RingUrl <zip 直链>；' + "`n" +
                       '  b) 把地址填进本脚本的 $DefaultRingUrl；' + "`n" +
                       '  c) CI 里配置 secret CBS_RING_URL。' + "`n" +
                       '（也可以用 -SkipRing 只取 ffmpeg）')
            }
            Write-Host ('=== 获取铃声包 ===' + "`n" + '  ' + $url)
            $zip = Join-Path $tmp 'ring.zip'
            try {
                # 有令牌 + 是 GitHub Release 地址 -> 走 API（私有仓库必需）
                $viaApi = $false
                if ($RingToken) { $viaApi = Get-GitHubReleaseAsset $url $RingToken $zip }
                if (-not $viaApi) { Get-WebFile $url $zip }
            } catch {
                $st = ''
                if ($_.Exception.Response) { $st = 'HTTP ' + [int]$_.Exception.Response.StatusCode + ' ' }
                # 铃声是"锦上添花"：拿不到就用 make-bells 合成，或干脆不带铃声。
                # 不能让整条构建因为一个可选资源挂掉（CI 上就踩过：私有仓库缺令牌 → 401/404 → 全红）。
                Write-Host ('  [警告] 铃声包下载失败（' + $st + $_.Exception.Message + '）') -ForegroundColor Yellow
                Write-Host '         构建会继续：可用 tools\make-bells.ps1 合成开源铃声，或稍后补配 CBS_ASSET_TOKEN。' -ForegroundColor Yellow
                if ($RequireRing) { throw '铃声包下载失败（-RequireRing 要求必须有）' }
                $zip = $null
            }
            if (-not $zip) { Write-Host '  已跳过铃声（构建继续）' -ForegroundColor Yellow }
            else {
            New-Item -ItemType Directory -Force -Path $ringDir | Out-Null
            Expand-Archive -Path $zip -DestinationPath $tmp -Force
            # 压缩包里可能多一层目录，递归找音频文件并摊平到 third_party\ring
            $audio = Get-ChildItem $tmp -Recurse -File -Include '*.mp3', '*.MP3', '*.wav', '*.flac' -ErrorAction SilentlyContinue |
                     Where-Object { $_.DirectoryName -notlike '*cbs-deps-*ring.zip*' }
            $n = 0
            foreach ($f in $audio) { Copy-Item $f.FullName (Join-Path $ringDir $f.Name) -Force; $n++ }
            if ($n -eq 0) { throw '铃声包里没有找到音频文件（.mp3/.wav/.flac）' }
            Write-Host ('  [OK] 已放入 third_party\ring（' + $n + ' 个音频）') -ForegroundColor Green
        }
    }

    }

    # ---------------- 校验 ----------------
    Write-Host ''
    Write-Host '=== 校验 ==='
    $ok = $true
    foreach ($n in @('ffmpeg.exe', 'ffplay.exe', 'ffprobe.exe')) {
        $has = Test-Path (Join-Path $ffDir $n)
        if (-not $has) { $ok = $false }
        Write-Host ('  ' + $n.PadRight(12) + $(if ($has) { 'OK' } else { '缺失' }))
    }
    $mp3 = @(Get-ChildItem $ringDir -Filter '*.mp3' -ErrorAction SilentlyContinue).Count
    Write-Host ('  铃声         ' + $mp3 + ' 个')
    if (-not $ok) { throw '依赖不完整' }
    Write-Host ''
    Write-Host '依赖就绪。' -ForegroundColor Green
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}