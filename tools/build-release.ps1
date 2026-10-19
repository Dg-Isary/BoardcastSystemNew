# 智能校园广播系统 · 一键发布脚本（先跑门禁，再发布、核对产物、编译安装包）
# 注意：本文件必须保存为"UTF-8 带 BOM"。Windows PowerShell 5.1 在没有 BOM 时会按 GBK 解读中文，直接报语法错误。
param(
    [string]$Root = '',
    [switch]$SkipPublish,
    # 可选：用指定的 .ico 覆盖 csproj 里的 <ApplicationIcon>（开源构建用自动生成的 blank.ico）。
    # 走 MSBuild 全局属性覆盖，**不会改动或覆盖工程里已有的 broadcasting.ico**。
    [string]$IconOverride = '',
    # 跳过安装包编译（只产出 publish 目录）。tools\build-opensource.ps1 会按需传入。
    [switch]$SkipInstaller,
    # 跳过"发布产物离线自检"这道门禁（调试用；正式出包不要跳）。
    [switch]$SkipSelfTest,
    # 安装包文件名后缀（默认空 = BroadcastSystem_Setup_v{版本}）。
    # 开源构建传 "_opensource"，这样开源包与闭源包可以同时留在 Output\ 里互不覆盖。
    [string]$OutputBaseSuffix = ''
)

# 一键构建发布产物 + 编译安装包。
#   .\tools\build-release.ps1
# 说明：
#   1) 发布前会由 csproj 里的目标在发布后补齐 res 资源，避免"重复发布把 res\ring 删空"导致安装包编译失败；
#   2) 打包前强制做前端语法检查（node），避免"构建成功但看板整体失效"这种只有浏览器才看得出的问题；
#   3) 发布后本脚本会逐项核对关键文件，核对不通过直接报错退出，不让你编译出一个残缺的安装包；
#   4) 若未安装 Inno Setup 6，则只产出 publish 目录并提示安装。

$ErrorActionPreference = 'Stop'
# 仓库根：默认取本脚本所在目录的上一级，这样换机器/换路径克隆后不用改脚本。
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path $Root)) { throw "找不到仓库根目录: $Root" }
$proj = Join-Path $Root 'BroadcastSystemNet\BroadcastSystemNet.csproj'
$iss = Join-Path $Root 'BroadcastSystemNet.iss'
$pub = Join-Path $Root 'BroadcastSystemNet\bin\Release\net8.0-windows\publish'

if (-not (Test-Path $proj)) { throw "找不到工程文件: $proj" }
if (-not (Test-Path $iss)) { throw "找不到安装脚本: $iss" }

# ---------- 打包前的强制门禁 1：本目录 PowerShell 脚本的编码与语法 ----------
Write-Host '=== [1/5] 脚本自检（编码 + 语法）===' -ForegroundColor Cyan
$scriptFailed = @()
foreach ($f in (Get-ChildItem (Join-Path $Root 'tools') -Filter '*.ps1')) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if (-not $hasBom) {
        # 含中文的 .ps1 若无 BOM，Windows PowerShell 会按 ANSI 解析而报语法错误
        $txt = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
        [System.IO.File]::WriteAllText($f.FullName, $txt, (New-Object System.Text.UTF8Encoding($true)))
        Write-Host ("  已为 {0} 补写 UTF-8 BOM" -f $f.Name) -ForegroundColor Yellow
    }
    $perr = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$perr)
    if ($perr -and $perr.Count -gt 0) {
        $scriptFailed += $f.Name
        Write-Host ("  [失败] {0}: {1}" -f $f.Name, $perr[0].Message) -ForegroundColor Red
    } else {
        Write-Host ("  [通过] {0}" -f $f.Name) -ForegroundColor Green
    }
}
if ($scriptFailed.Count -gt 0) { throw ("脚本语法检查未通过：" + ($scriptFailed -join '、')) }

# ---------- 打包前的强制门禁 2：前端内联脚本语法 ----------
Write-Host '=== [2/5] 前端语法检查 ===' -ForegroundColor Cyan
$frontendCheck = Join-Path $Root 'tools\check-frontend.ps1'
if (Test-Path $frontendCheck) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $frontendCheck -Root $Root
    if ($LASTEXITCODE -ne 0) {
        throw "前端语法检查未通过：看板会整体失效，已中止打包。请先修复 res\index.html 或 res\mobile.html 的内联脚本。"
    }
} else {
    Write-Host '  未找到 tools\check-frontend.ps1，跳过前端语法检查' -ForegroundColor Yellow
}

# ---------- 打包前的强制门禁 3：桌面端 Font Awesome 码点 ----------
Write-Host '=== [2/5] 桌面端图标码点检查 ===' -ForegroundColor Cyan
$iconCheck = Join-Path $Root 'tools\check-desktop-icons.ps1'
if (Test-Path $iconCheck) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $iconCheck -Root $Root
    if ($LASTEXITCODE -ne 0) {
        throw "桌面端图标码点检查未通过：存在 FA7 图标集之外的码点，界面会显示成方块，已中止打包。"
    }
} else {
    Write-Host '  未找到 tools\check-desktop-icons.ps1，跳过图标码点检查' -ForegroundColor Yellow
}

# ---------- 打包前的强制门禁 4：禁止 Emoji（界面一律用 Font Awesome 图标） ----------
Write-Host '=== [2/5] Emoji 检查 ===' -ForegroundColor Cyan
$emojiCheck = Join-Path $Root 'tools\check-no-emoji.ps1'
if (Test-Path $emojiCheck) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $emojiCheck -Root $Root
    if ($LASTEXITCODE -ne 0) {
        throw "Emoji 检查未通过：界面禁止 Emoji（Windows 会渲染成彩色图形），请改用 Font Awesome 图标或纯文字。"
    }
} else {
    Write-Host '  未找到 tools\check-no-emoji.ps1，跳过 Emoji 检查' -ForegroundColor Yellow
}

if (-not $SkipPublish) {
    Write-Host '=== [3/5] 发布（自包含单文件）===' -ForegroundColor Cyan
    # 先清空 publish：dotnet publish 是**增量**语义，上一次的残留会留在目录里，
    # 表现为安装包体积在 131~134 MB 间跳动、甚至把过期二进制打进包（report.md H1）。
    if (Test-Path $pub) {
        Remove-Item $pub -Recurse -Force
        Write-Host '  已清空旧的 publish 目录（避免增量残留被打进安装包）' -ForegroundColor Yellow
    }
    $pubArgs = @($proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                 '-p:PublishSingleFile=true', '-o', $pub, '--nologo')
    if ($IconOverride) {
        if (-not (Test-Path $IconOverride)) { throw "指定的替代图标不存在: $IconOverride" }
        $pubArgs += ('-p:ApplicationIcon=' + (Resolve-Path $IconOverride).Path)
        Write-Host ('  使用替代图标（不覆盖工程内原图标）: {0}' -f (Resolve-Path $IconOverride).Path) -ForegroundColor Yellow
    }
    & dotnet publish @pubArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
}

Write-Host '=== [4/5] 核对发布产物 ===' -ForegroundColor Cyan
$checks = @(
    @{ Name = '主程序 exe';      Path = "$pub\BroadcastSystemNet.exe"; Min = 100MB },
    @{ Name = '音频引擎 ffplay'; Path = "$pub\res\bin\ffplay.exe";     Min = 1MB },
    @{ Name = 'ffmpeg';          Path = "$pub\res\bin\ffmpeg.exe";     Min = 100KB },
    @{ Name = '网页看板';        Path = "$pub\res\index.html";         Min = 10KB },
    @{ Name = '手机端页面';      Path = "$pub\res\mobile.html";        Min = 5KB },
    @{ Name = 'Chart.js';        Path = "$pub\res\lib\chart.umd.min.js"; Min = 50KB },
    @{ Name = 'Flatpickr';       Path = "$pub\res\lib\flatpickr.min.js"; Min = 10KB },
    @{ Name = 'Font Awesome CSS';Path = "$pub\res\lib\fontawesome-all.min.css"; Min = 20KB },
    @{ Name = 'FA 字体';         Path = "$pub\res\lib\webfonts\fa-solid-900.woff2"; Min = 20KB }
)
$failed = @()
foreach ($c in $checks) {
    if ((Test-Path $c.Path) -and (Get-Item $c.Path).Length -ge $c.Min) {
        Write-Host ("  OK    {0,-16} {1,10:N0} 字节" -f $c.Name, (Get-Item $c.Path).Length) -ForegroundColor Green
    } else {
        Write-Host ("  缺失  {0,-16} {1}" -f $c.Name, $c.Path) -ForegroundColor Red
        $failed += $c.Name
    }
}
$ringCount = (Get-ChildItem "$pub\res\ring" -File -ErrorAction SilentlyContinue).Count
$cfgCount = (Get-ChildItem "$pub\res\config" -File -ErrorAction SilentlyContinue).Count
Write-Host ("  OK    铃声文件          {0} 个" -f $ringCount) -ForegroundColor Green
Write-Host ("  OK    配置模板          {0} 个" -f $cfgCount) -ForegroundColor Green
if ($ringCount -eq 0) { $failed += '铃声文件(ring)' }
if ($cfgCount -eq 0) { $failed += '配置模板(config)' }
if ($failed.Count -gt 0) { throw ("发布产物不完整，缺少：" + ($failed -join '、') + "。请勿继续编译安装包。") }

# ---------- 打包前的强制门禁 5：离线自检（R3） ----------
# 用**刚发布的产物**跑，确保"要发出去的这个包"自身是绿的；失败即中止打包。
# 自检会把数据目录重定向到临时沙箱，不联网、不动真实数据。
# 注意：环境依赖项（缺中文语音包 / ffmpeg 被安全软件拦住）会记为 SKIP 而不是 FAIL，不会误伤门禁。
if ($SkipSelfTest) {
    Write-Host '=== [4/5] 离线自检（发布产物）===  已按要求跳过（-SkipSelfTest）' -ForegroundColor Yellow
} else {
    Write-Host '=== [4/5] 离线自检（发布产物）===' -ForegroundColor Cyan
    $stDir = Join-Path $env:TEMP ('cbs-selftest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $stOut = Join-Path $env:TEMP ('cbs-selftest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    try {
        New-Item -ItemType Directory -Force -Path $stDir | Out-Null
        & (Join-Path $pub 'BroadcastSystemNet.exe') --self-test --workdir $stDir --out $stOut | Out-Null
        $stRc = $LASTEXITCODE
        $stLine = ''
        if (Test-Path $stOut) {
            $m = Select-String -Path $stOut -Pattern '结果:' -Encoding UTF8 | Select-Object -First 1
            if ($m) { $stLine = $m.Line.Trim() }
        }
        if ($stRc -ne 0) {
            $stFails = @()
            if (Test-Path $stOut) {
                $stFails = Select-String -Path $stOut -Pattern '\[FAIL\]' -Encoding UTF8 | ForEach-Object { $_.Line.Trim() }
            }
            throw ("离线自检未通过（退出码 $stRc）：" + $stLine + "`n" + ($stFails -join "`n"))
        }
        Write-Host ("  通过  {0}" -f $(if ($stLine) { $stLine } else { '自检退出码 0' })) -ForegroundColor Green
    } finally {
        Remove-Item $stDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $stOut -Force -ErrorAction SilentlyContinue
    }
}

if ($SkipInstaller) {
    Write-Host '=== [5/5] 编译安装包 ===  已按要求跳过（-SkipInstaller）' -ForegroundColor Yellow
    Write-Host ("发布产物已就绪: " + $pub)
} else {
    Write-Host '=== [5/5] 编译安装包 ===' -ForegroundColor Cyan
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        Write-Host '未找到 Inno Setup 6 的 ISCC.exe，已跳过打包。' -ForegroundColor Yellow
        Write-Host '请安装 Inno Setup 6（https://jrsoftware.org/isdl.php）后重新运行本脚本。' -ForegroundColor Yellow
        Write-Host ("发布产物已就绪: " + $pub)
        exit 0
    }
    # 版本号唯一来源：csproj 的 <Version>；这里读出来传给 Inno Setup，避免界面/安装包两处手工同步。
    $appVersion = '4.0.0'
    try {
        $csproj = Join-Path $Root 'BroadcastSystemNet\BroadcastSystemNet.csproj'
        if (Test-Path $csproj) {
            $m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
            if ($m) { $appVersion = $m.Matches[0].Groups[1].Value.Trim() }
        }
    } catch { }
    Write-Host ("  安装包版本号：" + $appVersion) -ForegroundColor DarkGray
    # 输出文件名：默认 BroadcastSystem_Setup_v{版本}；传了 -OutputBaseSuffix 就追加后缀（开源包用）。
    $isccArgs = @(("/DAppVersion=" + $appVersion))
    if ($OutputBaseSuffix) {
        $outBase = 'BroadcastSystem_Setup_v' + $appVersion + $OutputBaseSuffix
        $isccArgs += ("/DOutputBase=" + $outBase)
        Write-Host ("  输出文件名：" + $outBase) -ForegroundColor DarkGray
    }
    & $iscc @isccArgs $iss
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }
    Write-Host '安装包已生成，位置：' -ForegroundColor Green
    Get-ChildItem (Join-Path $Root 'Output') -Filter '*.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 3 |
        ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
}
