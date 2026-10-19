# 智能校园广播系统 · 发布公开快照（重建无历史的 public 分支并强制推送）
# ==========================================================================
# 为什么需要它：本地 main 的完整历史里含早期未脱敏的配置与文档（真实目录、设备 ID、
# docs/ 里的服务器信息等），直接推 main 会把它们一起公开。本脚本的做法是：
#   ① 先**扫描敏感串**，命中就中止（宁可不出包，也不泄露）；
#   ② 用当前工作树重建一个**只有 1 个提交**的 public 分支（不含任何历史）；
#   ③ 强制推送到远程 main。
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\tools\publish-snapshot.ps1
#   ... -DryRun                 只扫描 + 重建本地 public，不推送
#   ... -Message "自定义提交信息"
#   ... -Proxy "http://proxy-host:port"    本机无外网时走代理
#
# 注意：本文件必须保存为 UTF-8 带 BOM（build-release.ps1 的脚本自检会检查）。

param(
    [string]$Root = '',
    [string]$Message = '',
    [string]$Proxy = '',
    # 发布成功后，在**快照提交**上打一个标签（如 v4.1.0）。留空则不打。
    # 必须在快照提交上打，不能在本地 main 上打 —— 否则 git push <tag> 会把私有历史一起推上去。
    [string]$Tag = '',
    [switch]$DryRun,
    [switch]$AllowDirty          # 工作区有未提交改动时也继续（默认拒绝，避免推出半成品）
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path $Root)) { throw "找不到仓库根目录: $Root" }
$git = 'git'

# ---------- 0) 工作区必须干净 ----------
Push-Location $Root
try {
    $dirty = @(& $git status --porcelain)
    if ($dirty.Count -gt 0 -and -not $AllowDirty) {
        Write-Host '工作区有未提交改动，先提交再发布（或加 -AllowDirty）：' -ForegroundColor Yellow
        $dirty | ForEach-Object { Write-Host ('  ' + $_) }
        exit 1
    }

    # ---------- 1) 敏感串扫描（命中即中止） ----------
    Write-Host ''
    Write-Host '=== 1/4 敏感串扫描 ===' -ForegroundColor Cyan
    # 这些是本项目历史上出现过的敏感痕迹；命中任何一条都不发布。
    $patterns = @(
        '450923', '博白',
        'D:/歌曲音乐', 'D:/校长', 'D:/2023春广播',
        'C:/Users/', 'D:\\Users\\', 'D:/USER/',
        '192\.168\.112',            # 开发机的局域网/代理地址
        'proxyHost',                # gradle 代理配置
        'wxid_', '@qq\.com', '@163\.com',
        'Teacher'
    )
    $hits = 0
    foreach ($pat in $patterns) {
        # 排除本脚本自身：它的敏感词清单里就写着这些串，不排除会"自己抓自己"。
        # 白名单：本脚本自身（词表里就写着这些串）+ 第三方语言包（含译者邮箱，与本项目无关）
        $found = @(& $git grep -I -l -E $pat -- ':!tools/publish-snapshot.ps1' ':!installer/*.isl' 2>$null)
        if ($found.Count -gt 0) {
            $hits += $found.Count
            Write-Host ('  ★ 命中 ' + $pat + '：') -ForegroundColor Red
            $found | ForEach-Object { Write-Host ('      ' + $_) }
        }
    }
    if ($hits -gt 0) {
        Write-Host ''
        Write-Host '敏感串扫描未通过，已中止（不会推送）。' -ForegroundColor Red
        exit 1
    }
    Write-Host '  未发现敏感串 ✓' -ForegroundColor Green

    # ---------- 2) 重建 public 分支 ----------
    Write-Host ''
    Write-Host '=== 2/4 重建 public 快照分支 ===' -ForegroundColor Cyan
    $cur = (& $git rev-parse --abbrev-ref HEAD).Trim()
    # 【保留发布历史】如果远程已有 main，就以它为父节点追加一个提交 —— 这样 GitHub 上能看出
    # "每个版本一次提交"的变化；父节点是**上一次快照**，所以仍然不含任何私有历史。
    # （以前用 --orphan 每次重建，导致仓库永远只有 1 个叫"初始提交"的提交，看不出改了什么。）
    # 【首次推送】新仓库还没有 main 时，git fetch 会报 "couldn't find remote ref main"。
    # 在 $ErrorActionPreference='Stop' 下这会被当成终止错误直接中止脚本 —— 所以这里临时放宽。
    $eapSave = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $git fetch origin main --quiet 2>&1 | Out-Null
    $ErrorActionPreference = $eapSave
    $hasRemote = (& $git rev-parse --verify --quiet 'origin/main') -ne $null
    $hasPublic = @(& $git branch --format='%(refname:short)' | Where-Object { $_ -eq 'public' }).Count -gt 0
    if ($hasPublic) { & $git branch -D public --quiet }
    if ($hasRemote) {
        # 关键：先切到 origin/main（父节点 = 上一次快照），再把**本地分支的内容**灌进索引与工作树。
        # 只用 checkout 会把工作树也切成上一次快照的内容 -> 提交变成空提交、git commit 静默失败。
        & $git checkout -B public 'origin/main' --quiet
        & $git read-tree -u --reset $cur
    } else {
        & $git checkout --orphan public --quiet
        & $git reset --mixed --quiet
    }
    & $git add -A
    if (-not $Message) { $Message = '快照：智能校园广播系统（开源快照）' }
    & $git commit --quiet -m $Message
    if ($LASTEXITCODE -ne 0) { Write-Host '  提交失败（没有改动？）' -ForegroundColor Red; exit 1 }
    $cnt = @(& $git rev-list --count public)
    $files = @(& $git ls-tree -r --name-only public).Count
    Write-Host ('  提交数 ' + $cnt + '，文件数 ' + $files) -ForegroundColor Green
    & $git checkout $cur --quiet

    # ---------- 3) 推送 ----------
    Write-Host ''
    Write-Host '=== 3/4 推送 ===' -ForegroundColor Cyan
    if ($DryRun) {
        Write-Host '  [DryRun] 不推送。要推送请去掉 -DryRun。' -ForegroundColor Yellow
    } else {
        $args = @('push', '-f', 'origin', 'public:main')
        if ($Proxy) { $args = @('-c', ('http.proxy=' + $Proxy), '-c', ('https.proxy=' + $Proxy)) + $args }
        & $git @args
        if ($LASTEXITCODE -ne 0) { Write-Host '  推送失败（见上方 git 输出）' -ForegroundColor Red; exit 1 }
        Write-Host '  已推送 ✓' -ForegroundColor Green

        # 【标签必须打在快照提交上】
        # 本地 main 是私有历史（含早期未脱敏内容），远程 main 是完全独立的快照历史。
        # 如果先 `git tag v4.1.0`（默认打在当前 HEAD = 本地 main）再 push，git 会把
        # 该标签引用的整条**私有历史**一起推到远程 —— 等于把敏感内容全公开。
        # 所以这里显式把标签打在 `public` 分支（= 刚推上去的快照提交）上。
        if ($Tag) {
            & $git tag -d $Tag --quiet 2>$null
            & $git tag -s $Tag -m $Tag public 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host '  签名标签失败（GPG 未就绪？），回退为普通标签' -ForegroundColor Yellow
                & $git tag -a $Tag -m $Tag public
            }
            if ($LASTEXITCODE -ne 0) { Write-Host '  打标签失败' -ForegroundColor Red; exit 1 }
            & $git push origin $Tag
            if ($LASTEXITCODE -ne 0) { Write-Host '  推送标签失败' -ForegroundColor Red; exit 1 }
            Write-Host ('  已在快照提交上打标签 ' + $Tag + ' 并推送（会自动触发 CI 的 release 作业）') -ForegroundColor Green
        }
    }

    # ---------- 4) 汇总 ----------
    Write-Host ''
    Write-Host '=== 4/4 结果 ===' -ForegroundColor Cyan
    Write-Host ('  本地 public : ' + $cnt + ' 个提交 / ' + $files + ' 个文件（无历史）')
    Write-Host ('  本地 ' + $cur + ' : 完整历史，仅本地保留，不公开')
    if (-not $DryRun) { Write-Host '  远程 main   : 已被快照覆盖（1 个提交）' }
    Write-Host ''
} finally {
    # 【兜底】无论成功还是中途 exit，都切回原来的分支。
    # 不加这段的话，任何一次失败都会把仓库留在 public 分支上 -> 下次运行删不掉 public、
    # 而且后续提交会悄悄提到 public 而不是 main（实测踩过）。
    if ($cur) { & $git checkout $cur --quiet 2>$null }
    Pop-Location
}