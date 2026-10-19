# 智能校园广播系统 · 一次产出四个发布包
# ==========================================================================
# 产物（互不覆盖）：
#   Output\BroadcastSystem_Setup_v{版本}.exe              闭源 桌面安装包
#   Output\BroadcastSystem_Setup_v{版本}_opensource.exe   开源 桌面安装包
#   dist\BroadcastApp.apk                                 闭源 安卓 APK
#   dist\BroadcastApp_opensource.apk                      开源 安卓 APK
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1
#   powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -SkipDesktop    # 只出两个 APK
#   powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -SkipAndroid    # 只出两个安装包
#   powershell -ExecutionPolicy Bypass -File .\tools\build-all.ps1 -SkipSelfTest   # 跳过离线自检（仅调试）
#
# 说明：
#   · 本脚本只做**编排**：实际工作交给 tools\build-release.ps1（闭源）与
#     tools\build-opensource.ps1（开源），所以四道静态门禁、产物核对、离线自检都照常执行。
#   · 开源与闭源的区别：开源构建用 blank.ico 占位图标、third_party\ffmpeg、附带合规声明，
#     并自动补齐"未入库资源"（程序图标 / ffmpeg / 铃声 / 安卓启动图标 / local.properties）。
#   · 开源安装包与开源 APK 用独立文件名（_opensource 后缀），不会覆盖闭源产物。
#   · 注意：本文件必须保存为"UTF-8 带 BOM"（build-release.ps1 的脚本自检会检查）。

param(
    [string]$Root = '',
    [switch]$SkipSelfTest,
    [switch]$SkipDesktop,
    [switch]$SkipAndroid
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path $Root)) { throw "找不到仓库根目录: $Root" }
if ($SkipDesktop -and $SkipAndroid) { throw "两个都跳过就没事可做了（-SkipDesktop 与 -SkipAndroid 不能同时用）" }

$outDir   = Join-Path $Root 'Output'
$distDir  = Join-Path $Root 'dist'
$toolsDir = Join-Path $Root 'tools'

# 版本号唯一来源：csproj 的 <Version>
$appVersion = '0.0.0'
try {
    $csproj = Join-Path $Root 'BroadcastSystemNet\BroadcastSystemNet.csproj'
    $m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if ($m) { $appVersion = $m.Matches[0].Groups[1].Value.Trim() }
} catch { }

$setupClosed = Join-Path $outDir  ('BroadcastSystem_Setup_v' + $appVersion + '.exe')
$setupOpen   = Join-Path $outDir  ('BroadcastSystem_Setup_v' + $appVersion + '_opensource.exe')
$apkClosed   = Join-Path $distDir 'BroadcastApp.apk'
$apkOpen     = Join-Path $distDir 'BroadcastApp_opensource.apk'

function Step([string]$text) {
    Write-Host ''
    Write-Host ('=== ' + $text + ' ===') -ForegroundColor Cyan
}

# 产物校验：
#   · 安装包：ISCC 直接写进 Output\，存在即本次产出（脚本退出码非 0 会另行记失败）。
#   · APK：两个构建都是"从 gradle 产物 copy 过来"，而 Windows 的 copy **会保留源文件的修改时间** ——
#     所以"时间有没有变新"判断不出来（gradle UP-TO-DATE 时源文件时间可能比本次构建还早）。
#     改为**比对 SHA256**：与 gradle 产物一致，才算这次真的复制成功。
function Get-GradleApk { Join-Path $Root 'BroadcastApp\android\app\build\outputs\apk\release\app-release.apk' }
function AssertApkCopied([string]$distPath) {
    if (-not (Test-Path $distPath)) { return $false }
    $src = Get-GradleApk
    if (-not (Test-Path $src)) { return $true }   # 没有可比对的源，只能要求"存在"
    try { return ((Get-FileHash $distPath -Algorithm SHA256).Hash -eq (Get-FileHash $src -Algorithm SHA256).Hash) } catch { return $true }
}

$failed = @()

# ---------- 1/4 闭源 桌面安装包 ----------
if (-not $SkipDesktop) {
    Step ('[1/4] 闭源 桌面安装包  ->  Output\' + (Split-Path $setupClosed -Leaf))
    $a = @('-Root', $Root)
    if ($SkipSelfTest) { $a += '-SkipSelfTest' }
    # 【图标统一】闭源版也用开源图标（third_party\Opensource.svg 转制）作为**程序图标**；
    # 只有 favicon.ico / favicon.svg 保持闭源原样（用户明确要求）。
    $sharedIco = Join-Path $Root 'tools\opensource\icons\app.ico'
    if (-not (Test-Path $sharedIco)) {
        $mk = Join-Path $Root 'tools\make-icons.ps1'
        if (Test-Path $mk) { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $mk -Root $Root 2>&1 | Out-Null }
    }
    if (Test-Path $sharedIco) { $a += @('-IconOverride', $sharedIco); Write-Host ('  程序图标: ' + $sharedIco) -ForegroundColor DarkGray }
# 开源包用**占位图标**（脚本会先备份真图标、构建完自动恢复），这样开源包与闭源包确实不同，
# 也符合"从公开源码构建"的语义（真图标不入库）。
if (-not $SkipAndroid) { $a += '-ForcePlaceholderIcons' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'build-release.ps1') @a
    if ($LASTEXITCODE -ne 0) { $failed += '闭源桌面安装包（脚本失败）' }
    elseif (-not (Test-Path $setupClosed)) { $failed += '闭源桌面安装包（没有产物）' }
}

# ---------- 2/4 闭源 安卓 APK ----------
if (-not $SkipAndroid) {
    Step '[2/4] 闭源 安卓 APK      ->  dist\BroadcastApp.apk'
    & cmd.exe /c ('cd /d "' + $Root + '" && BroadcastApp\build-android.bat release')
    if ($LASTEXITCODE -ne 0) { $failed += '闭源安卓 APK（脚本失败）' }
    elseif (-not (AssertApkCopied $apkClosed)) { $failed += '闭源安卓 APK（产物与 gradle 不一致）' }
}

# ---------- 3/4 + 4/4 开源：一个脚本同时出安装包与 APK ----------
Step '[3/4][4/4] 开源 桌面安装包 + 安卓 APK（_opensource 后缀）'
$a = @()
if ($SkipDesktop) { $a += '-AndroidOnly' }
if ($SkipAndroid) { $a += '-DesktopOnly' }
if ($SkipSelfTest) { $a += '-SkipSelfTest' }
# 开源包用**占位图标**（脚本会先备份真图标、构建完自动恢复），这样开源包与闭源包确实不同，
# 也符合"从公开源码构建"的语义（真图标不入库）。
if (-not $SkipAndroid) { $a += '-ForcePlaceholderIcons' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'build-opensource.ps1') @a
if ($LASTEXITCODE -ne 0) { $failed += '开源构建（脚本失败）' }
else {
    if (-not $SkipDesktop -and -not (Test-Path $setupOpen))    { $failed += '开源桌面安装包（没有产物）' }
    if (-not $SkipAndroid -and -not (AssertApkCopied $apkOpen)) { $failed += '开源安卓 APK（产物与 gradle 不一致）' }
}

# ---------- 汇总 ----------
Write-Host ''
Write-Host '############################################################' -ForegroundColor DarkCyan
Write-Host ('#  四个发布包（版本 ' + $appVersion + '）') -ForegroundColor DarkCyan
Write-Host '############################################################' -ForegroundColor DarkCyan
foreach ($p in @($setupClosed, $setupOpen, $apkClosed, $apkOpen)) {
    $rel = $p.Replace($Root + '\', '')
    if (Test-Path $p) {
        $f = Get-Item $p
        $line = '  OK    ' + $rel.PadRight(46) + '  ' + ('{0,8:N2} MB' -f ($f.Length / 1MB)) + '  ' + $f.LastWriteTime.ToString('MM-dd HH:mm:ss')
        Write-Host $line -ForegroundColor Green
    } else {
        Write-Host ('  缺失  ' + $rel) -ForegroundColor Red
    }
}
Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host ('构建失败：' + ($failed -join '；')) -ForegroundColor Red
    exit 1
}
Write-Host '四个包全部产出完成。' -ForegroundColor Green
exit 0
