# 智能校园广播系统 · 一键升版本号
# ==========================================================================
# 版本号散落在多个地方，手工改容易漏（尤其安卓的 versionCode/versionName 是独立的，
# 不从 csproj 派生）。本脚本一次改全，并打印"改了哪些地方"。
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\tools\bump-version.ps1 -Version 4.1.0
#   powershell -ExecutionPolicy Bypass -File .\tools\bump-version.ps1 -Version 4.0.1 -DryRun
#
# 会改这些文件：
#   1) BroadcastSystemNet\BroadcastSystemNet.csproj   <Version>            （**版本号唯一来源**）
#   2) BroadcastSystemNet.iss                          #define AppVersion + AppVerName
#   3) BroadcastApp\android\app\build.gradle.kts       versionCode + versionName
#   4) VERSION.md                                      "当前版本" 那一行 + 安装包文件名
#   5) README.md                                       "当前版本 **x**" + 安装包文件名
#
# 说明：
#   · 安装包文件名（BroadcastSystem_Setup_v{版本}.exe）由 .iss 自动拼出来，不用单独改；
#   · 安卓 versionCode 按 major*10000 + minor*100 + patch 递增（4.1.0 -> 40100），必须单调递增；
#   · 历史叙述里出现的旧版本号（如"4.0.0 新增"）**不会**被改动，只改"当前版本"这类当前态表述。
#   · 本文件必须保存为 UTF-8 带 BOM（build-release.ps1 的脚本自检会检查）。

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Root = '',
    [switch]$DryRun,
    # 改完顺手打个标签（有 GPG 密钥就用签名标签 -s，否则用普通标签 -a 并提示）
    [switch]$Tag
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path $Root)) { throw "找不到仓库根目录: $Root" }

# ---------- 校验版本号格式 ----------
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "版本号格式不对：'$Version'，应为 主.次.修订（例如 4.1.0）"
}
$major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
$versionCode = $major * 10000 + $minor * 100 + $patch
$shortVersion = "$major.$minor"     # 安卓 versionName / 安装包显示名用短版本

Write-Host ''
Write-Host ('=== 升版本号 -> ' + $Version + '（安卓 versionCode=' + $versionCode + '）===')
if ($DryRun) { Write-Host '  [DryRun] 只显示将要改动，不写文件' -ForegroundColor Yellow }

$changes = New-Object System.Collections.ArrayList

function Update-File {
    param([string]$Path, [scriptblock]$Transform, [string]$Label)

    if (-not (Test-Path $Path)) { Write-Host ('  [跳过] 找不到文件: ' + $Path) -ForegroundColor DarkYellow; return }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }

    $new = & $Transform $text
    if ($new -eq $text) {
        Write-Host ('  [未变] ' + $Label) -ForegroundColor DarkGray
        return
    }

    if (-not $DryRun) {
        [System.IO.File]::WriteAllText($Path, $new, (New-Object System.Text.UTF8Encoding($hasBom)))
    }
    Write-Host ('  [已改] ' + $Label) -ForegroundColor Green
    [void]$changes.Add($Label)
}

# ---------- 1) csproj：版本号唯一来源 ----------
Update-File -Path (Join-Path $Root 'BroadcastSystemNet\BroadcastSystemNet.csproj') -Label 'csproj <Version>' -Transform {
    param($t)
    $t -replace '<Version>[^<]*</Version>', ('<Version>' + $Version + '</Version>')
}

# ---------- 2) .iss：默认版本 + 显示名 ----------
Update-File -Path (Join-Path $Root 'BroadcastSystemNet.iss') -Label '.iss AppVersion / AppVerName' -Transform {
    param($t)
    $t = $t -replace '#define AppVersion "[^"]*"', ('#define AppVersion "' + $Version + '"')
    $t = $t -replace 'AppVerName=智能校园广播系统 [0-9.]+', ('AppVerName=智能校园广播系统 ' + $shortVersion)
    return $t
}

# ---------- 3) gradle.kts：versionCode + versionName ----------
Update-File -Path (Join-Path $Root 'BroadcastApp\android\app\build.gradle.kts') -Label 'gradle versionCode / versionName' -Transform {
    param($t)
    $t = $t -replace 'versionCode\s*=\s*\d+', ('versionCode = ' + $versionCode)
    $t = $t -replace 'versionName\s*=\s*"[^"]*"', ('versionName = "' + $shortVersion + '"')
    return $t
}

# ---------- 4) VERSION.md：当前版本 + 安装包名 ----------
Update-File -Path (Join-Path $Root 'VERSION.md') -Label 'VERSION.md 当前版本' -Transform {
    param($t)
    $t = $t -replace '\| 当前版本 \| \*\*[0-9.]+\*\* \|', ('| 当前版本 | **' + $Version + '** |')
    $t = $t -replace 'BroadcastSystem_Setup_v[0-9.]+(?:_opensource)?\.exe', ('BroadcastSystem_Setup_v' + $Version + '.exe')
    return $t
}

# ---------- 5) README.md：当前版本 + 安装包名 ----------
Update-File -Path (Join-Path $Root 'README.md') -Label 'README.md 当前版本' -Transform {
    param($t)
    $t = $t -replace '> 当前版本 \*\*[0-9.]+\*\*', ('> 当前版本 **' + $Version + '**')
    $t = $t -replace 'BroadcastSystem_Setup_v[0-9.]+(?:_opensource)?\.exe', ('BroadcastSystem_Setup_v' + $Version + '.exe')
    return $t
}

# ---------- 汇总 ----------
Write-Host ''
if ($changes.Count -eq 0) {
    Write-Host '没有任何改动（可能已经是这个版本号了）。' -ForegroundColor DarkGray
} else {
    Write-Host ('共改动 ' + $changes.Count + ' 处：') -ForegroundColor Cyan
    foreach ($c in $changes) { Write-Host ('  - ' + $c) }
    Write-Host ''
    Write-Host '下一步：' -ForegroundColor Cyan
    Write-Host ('  1) 出包：powershell -File .\tools\build-all.ps1')
    if ($Tag) {
        # 有 GPG 签名密钥 -> 用签名标签（GitHub 上显示 Verified 徽章）；否则回退普通标签
        $signKey = (& git config --get user.signingkey) 2>$null
        $tagName = 'v' + $Version
        if ($signKey) {
            & git -C $Root tag -s $tagName -m $tagName
            if ($LASTEXITCODE -eq 0) { Write-Host ('  已创建**签名**标签 ' + $tagName + '（GPG: ' + $signKey + '）') -ForegroundColor Green }
            else { Write-Host '  签名失败（GPG 未就绪？），回退普通标签' -ForegroundColor Yellow; & git -C $Root tag -a $tagName -m $tagName }
        } else {
            & git -C $Root tag -a $tagName -m $tagName
            Write-Host ('  已创建普通标签 ' + $tagName + '（配 GPG 后可用签名标签：git config --global user.signingkey <KEY_ID>）') -ForegroundColor Yellow
        }
    } else {
        Write-Host ('  2) 打标签：git tag -s v' + $Version + ' -m "v' + $Version + '"   # 有 GPG 用 -s（签名），否则 -a')
    }
    Write-Host ('  3) 推上去：git push origin main --tags')
}
Write-Host ''