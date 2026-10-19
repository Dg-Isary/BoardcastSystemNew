<#
  智能校园广播系统 · 开源构建脚本（电脑端 + 安卓端，一个脚本搞定）
  ==========================================================================
  用途：从 **git 源码** 构建出可发布的安装包与 APK。
        全新克隆缺的那些"未入库资源"（程序图标 / ffmpeg / 初始铃声 / 安卓启动图标 /
        local.properties）由本脚本自动补齐，**不需要手工准备任何文件**。

  用法（在仓库根目录）：
    powershell -ExecutionPolicy Bypass -File .\tools\build-opensource.ps1
    powershell -ExecutionPolicy Bypass -File .\tools\build-opensource.ps1 -MinimalRing
    powershell -ExecutionPolicy Bypass -File .\tools\build-opensource.ps1 -DesktopOnly
    powershell -ExecutionPolicy Bypass -File .\tools\build-opensource.ps1 -AndroidOnly
    powershell -ExecutionPolicy Bypass -File .\tools\build-opensource.ps1 -FfmpegDir D:\ffmpeg\bin

  参数：
    -DesktopOnly / -AndroidOnly   只构建一端（默认两端都建）
    -MinimalRing                  不复制第三方铃声，改为**手写静音占位铃声**
                                  （安装包照常编译通过，但不含任何第三方音频；按需使用）
    -SkipInstaller                只产出 publish 目录，不编译安装包（未装 Inno Setup 时用）
    -SkipSelfTest                 跳过发布产物的离线自检
    -NoNotice                     不生成"第三方组件声明.txt"（不推荐）
    -FfmpegDir <dir>              ffmpeg 二进制目录（默认自动搜索，见下）
    -RingDir <dir>                初始铃声目录（默认 third_party\ring，其次 res\ring）
    -IconFile <ico>               自定义程序图标（默认自动生成 blank.ico）
    -JavaHome / -GradleBin / -AndroidSdk   手动指定安卓工具链（默认自动探测）
    -Keystore / -KeyAlias / -StorePass / -KeyPass
                                  安卓**发布签名**（也可用同名环境变量 CBS_KEYSTORE /
                                  CBS_KEY_ALIAS / CBS_KEYSTORE_PASSWORD / CBS_KEY_PASSWORD）。
                                  都不给时回退 debug 签名（不能上架、不同机器构建的 APK 无法互相覆盖安装）。
                                  生成密钥库（一次即可，务必保管好，丢了就无法覆盖升级）：
                                    keytool -genkeypair -v -keystore cbs-release.jks -alias cbs `
                                            -keyalg RSA -keysize 2048 -validity 10000

  ffmpeg 来源（按顺序找，第一个满足的即用；**任何情况下都不会写进 git**）：
    1) -FfmpegDir 指定的目录
    2) third_party\ffmpeg
    3) BroadcastSystemNet\res\bin
    4) %USERPROFILE%\Downloads\ffmpeg*\bin  以及  %USERPROFILE%\Downloads\ffmpeg*
    5) C:\ffmpeg\bin
    6) PATH 上的 ffmpeg（取其所在目录）
    目录里必须同时有 ffmpeg.exe、ffplay.exe、ffprobe.exe。

  合规性打包（本脚本自动完成）：
    · 读取 ffmpeg 的构建配置，判定它是 LGPL 还是 GPL 构建，并**照实打印**；
    · 把 ffmpeg 随附的 LICENSE / COPYING 一并放进发布产物；
    · 生成"第三方组件声明.txt"（各组件许可 + FFmpeg 源码获取方式 + 可替换声明）；
    · 检测到 GPL / nonfree 构建时给出醒目提示（是否分发由使用者决定）。

  设计说明：
    · 发布由本脚本自己做（为了给 <ApplicationIcon> 传自动生成的 blank.ico，
      且**不覆盖**工程里可能存在的真实图标）；
    · 发布完成后把 ffmpeg / 铃声**直接放进 publish 目录**，因此不会污染源码目录；
    · 之后调用现有 tools\build-release.ps1 -SkipPublish 复用它的四道静态门禁、
      产物核对、发布产物离线自检与 Inno Setup 编译（不重复实现）。
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$DesktopOnly,
    [switch]$AndroidOnly,
    [switch]$MinimalRing,
    [switch]$SkipInstaller,
    [switch]$SkipSelfTest,
    [switch]$NoNotice,
    # 强制用**占位**安卓图标（即使本机有真图标）。用于让开源 APK 真正呈现'从公开源码构建'的样子；
    # 真图标会先备份、构建完自动恢复，不会破坏工作区。
    [switch]$ForcePlaceholderIcons,
    [string]$FfmpegDir = '',
    [string]$RingDir = '',
    [string]$IconFile = '',
    [string]$JavaHome = '',
    [string]$GradleBin = '',
    [string]$AndroidSdk = '',
    # 安卓发布签名（方案 B：密钥库与口令**从不写进仓库**，只从参数或环境变量注入）
    #   参数优先级高于同名环境变量：
    #     -Keystore   / CBS_KEYSTORE
    #     -KeyAlias   / CBS_KEY_ALIAS
    #     -StorePass  / CBS_KEYSTORE_PASSWORD
    #     -KeyPass    / CBS_KEY_PASSWORD
    # 都不提供时，Gradle 会回退到 debug 签名（脚本会给出醒目提示）。
    [string]$Keystore = '',
    [string]$KeyAlias = '',
    [string]$StorePass = '',
    [string]$KeyPass = ''
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 基础工具
$script:Warnings = New-Object System.Collections.ArrayList
$script:Notes    = New-Object System.Collections.ArrayList

function Step($n, $t) { Write-Host ''; Write-Host ("=== [{0}] {1} ===" -f $n, $t) -ForegroundColor Cyan }
function Ok($m)   { Write-Host ("  [OK]   " + $m) -ForegroundColor Green }
function Info($m) { Write-Host ("  " + $m) }
function Warn($m) { Write-Host ("  [注意] " + $m) -ForegroundColor Yellow; [void]$script:Warnings.Add($m) }
function Die($m)  { Write-Host ("  [失败] " + $m) -ForegroundColor Red; exit 1 }

# 把仓库根定在脚本所在目录的上一级：换机器/换路径克隆后无需改脚本
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path $Root)) { Die ("找不到仓库根目录: " + $Root) }
$Root = (Resolve-Path $Root).Path

$proj     = Join-Path $Root 'BroadcastSystemNet\BroadcastSystemNet.csproj'
$pub      = Join-Path $Root 'BroadcastSystemNet\bin\Release\net8.0-windows\publish'
$resBin   = Join-Path $Root 'BroadcastSystemNet\res\bin'
$resRing  = Join-Path $Root 'BroadcastSystemNet\res\ring'
$assets   = Join-Path $Root 'tools\opensource'
$appDir   = Join-Path $Root 'BroadcastApp'
$androidDir = Join-Path $appDir 'android'
$distDir  = Join-Path $Root 'dist'

if (-not (Test-Path $proj)) { Die ("找不到工程文件: " + $proj) }
New-Item -ItemType Directory -Force -Path $assets | Out-Null

Write-Host ''
Write-Host '############################################################' -ForegroundColor DarkCyan
Write-Host '#  智能校园广播系统 · 开源构建（电脑端 + 安卓端）' -ForegroundColor DarkCyan
Write-Host ('#  仓库: ' + $Root) -ForegroundColor DarkCyan
Write-Host '############################################################' -ForegroundColor DarkCyan

# ================================================================ 1. 环境探测
Step '1/6' '环境探测'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $dotnet) { Die '.NET SDK 未找到：请安装 .NET SDK 8 或更高版本（https://dotnet.microsoft.com/download）' }
$dotnetVer = (& dotnet --version) 2>$null
Ok ("dotnet {0}  ({1})" -f $dotnetVer, $dotnet.Source)

# --- Inno Setup（只在要出安装包时需要）
$iscc = $null
if (-not $SkipInstaller) {
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iscc) { Ok ("Inno Setup 6  (" + $iscc + ")") }
    else { Warn '未找到 Inno Setup 6：将只产出 publish 目录，不出安装包。下载：https://jrsoftware.org/isdl.php' }
}

# --- 安卓工具链（只在要构建 APK 时需要）
$needAndroid = -not $DesktopOnly
if ($needAndroid) {
    # JDK 17+
    if (-not $JavaHome) {
        $jhCandidates = @(
            $env:JAVA_HOME,
            (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr')
        ) + @(Get-ChildItem (Join-Path $env:ProgramFiles 'Eclipse Adoptium') -Directory -ErrorAction SilentlyContinue |
              ForEach-Object { $_.FullName }) +
          @(Get-ChildItem (Join-Path $env:ProgramFiles 'Java') -Directory -ErrorAction SilentlyContinue |
              ForEach-Object { $_.FullName })
        $JavaHome = $jhCandidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\java.exe')) } | Select-Object -First 1
    }
    if ($JavaHome) { Ok ("JDK  (" + $JavaHome + ")") } else { Warn '未找到 JDK 17+：安卓端将跳过（装 Android Studio 或 JDK 后重试，也可用 -JavaHome 指定）' }

    # Gradle：优先仓库自带的发行版，其次 PATH
    if (-not $GradleBin) {
        $gb = Join-Path $appDir 'tools\gradle-9.5.0\bin\gradle.bat'
        if (Test-Path $gb) { $GradleBin = $gb }
        else {
            $g = Get-Command gradle -ErrorAction SilentlyContinue
            if ($g) { $GradleBin = $g.Source }
        }
    }
    if ($GradleBin) { Ok ("Gradle  (" + $GradleBin + ")") }
    else { Warn '未找到 Gradle：安卓端将跳过。可把 Gradle 发行版放到 BroadcastApp\tools\gradle-9.5.0\，或装到 PATH，或用 -GradleBin 指定' }

    # Android SDK
    if (-not $AndroidSdk) {
        $lp = Join-Path $androidDir 'local.properties'
        if (Test-Path $lp) {
            $m = Select-String -Path $lp -Pattern '^\s*sdk\.dir\s*=\s*(.+)$' | Select-Object -First 1
            if ($m) {
                # local.properties 是 Java properties：':' 与 '\' 都被转义过，这里还原
                # （否则路径会被用成 "C\:\Users\..." 这种带反斜杠冒号的怪样子）
                $AndroidSdk = ($m.Matches[0].Groups[1].Value.Trim() -replace '\\\\', '\') -replace '\\:', ':'
            }
        }
        if (-not $AndroidSdk) {
            $AndroidSdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT,
                            (Join-Path $env:LOCALAPPDATA 'Android\Sdk'), 'C:\Android\Sdk') |
                          Where-Object { $_ -and (Test-Path (Join-Path $_ 'platforms')) } | Select-Object -First 1
        }
    }
    if ($AndroidSdk) { Ok ("Android SDK  (" + $AndroidSdk + ")") } else { Warn '未找到 Android SDK：安卓端将跳过（装 Android Studio，或设 ANDROID_HOME，或用 -AndroidSdk 指定）' }
}

$androidReady = $needAndroid -and $JavaHome -and $GradleBin -and $AndroidSdk

# ================================================================ 2. 图标
Step '2/6' '准备程序图标（自动生成 blank.ico）'

function New-BlankIco {
    param([string]$Path, [int]$Size = 32)
    # 手写一个合法的 32x32 / 32bpp ICO（纯白不透明）：ICONDIR + ICONDIRENTRY + BITMAPINFOHEADER + XOR + AND
    $xor = $Size * $Size * 4
    $and = [int]($Size * $Size / 8)
    $imgSize = 40 + $xor + $and
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)          # ICONDIR
    $bw.Write([byte]$Size); $bw.Write([byte]$Size)                            # 宽/高（32 记作 32）
    $bw.Write([byte]0); $bw.Write([byte]0)                                    # 调色板数 / 保留
    $bw.Write([uint16]1); $bw.Write([uint16]32)                               # 平面数 / 位深
    $bw.Write([uint32]$imgSize); $bw.Write([uint32]22)                        # 图像大小 / 偏移
    $bw.Write([uint32]40); $bw.Write([int32]$Size); $bw.Write([int32]($Size * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]$xor); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)                                # BITMAPINFOHEADER 结束
    for ($i = 0; $i -lt $Size * $Size; $i++) { $bw.Write([byte]255); $bw.Write([byte]255); $bw.Write([byte]255); $bw.Write([byte]255) }
    for ($i = 0; $i -lt $and; $i++) { $bw.Write([byte]0) }                    # AND 掩码全 0 = 不透明
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

function New-SilentMp3 {
    # 手写一个**静音 MP3**：MPEG-1 Layer III / 128 kbps / 44.1 kHz / 单声道。
    # 帧头固定 FF FB 90 C0，帧长 417 字节；帧内除 4 字节头外全 0，
    # 于是 side info 与 main data 全为 0，解码出来就是静音（已用 ffprobe 逐帧解码验证）。
    # 之所以不用 ffmpeg 生成：① 不依赖外部进程，任何机器都能跑；
    # ② 部分机器上安全软件会拦截 ffmpeg 写文件（实测 exit -13），那样生成必然失败。
    param([string]$Path, [double]$Seconds = 3.0)
    $frameLen = 417
    $samplesPerFrame = 1152
    $frames = [int][Math]::Ceiling($Seconds * 44100.0 / $samplesPerFrame)
    $buf = New-Object byte[] ($frameLen * $frames)
    for ($i = 0; $i -lt $frames; $i++) {
        $o = $i * $frameLen
        $buf[$o] = 0xFF; $buf[$o + 1] = 0xFB; $buf[$o + 2] = 0x90; $buf[$o + 3] = 0xC0
    }
    [System.IO.File]::WriteAllBytes($Path, $buf)
    return $frames
}

if (-not $IconFile) {
    $IconFile = Join-Path $assets 'blank.ico'
    if (-not (Test-Path $IconFile)) { New-BlankIco -Path $IconFile }
}
if (-not (Test-Path $IconFile)) { Die ("图标文件不存在: " + $IconFile) }
Ok ("程序图标: " + $IconFile)

# 网页看板的站点图标（res\favicon.ico 未入库；缺了只是浏览器标签页没图标，不影响功能）
$favicon = Join-Path $Root 'BroadcastSystemNet\res\favicon.ico'
if (-not (Test-Path $favicon)) { New-BlankIco -Path $favicon; Ok '已生成 res\favicon.ico（占位）' }
else { Info 'res\favicon.ico 已存在，保留不动' }

# ================================================================ 3. ffmpeg
# ffmpeg 是**电脑端**依赖（打铃/喊话的播放器），且按合规要求不进 git。
# 只构建安卓端时必须整段跳过 —— 否则纯 APK 构建会卡在"找不到 ffmpeg"上（GitHub Actions 就踩了这个）。
# 注意：下面这块**故意不缩进** —— 块内有 here-string，PowerShell 要求 "@ 必须在行首。
if (-not $AndroidOnly) {
# ================================================================ 3. ffmpeg
Step '3/6' '定位 ffmpeg（合规打包，不进 git）'

function Test-FfmpegDir {
    param([string]$Dir)
    if (-not $Dir -or -not (Test-Path $Dir)) { return $false }
    foreach ($n in @('ffmpeg.exe', 'ffplay.exe', 'ffprobe.exe')) {
        if (-not (Test-Path (Join-Path $Dir $n))) { return $false }
    }
    return $true
}

function Find-Ffmpeg {
    param([string]$Explicit)
    if ($Explicit) {
        if (Test-FfmpegDir $Explicit) { return (Resolve-Path $Explicit).Path }
        Die ("-FfmpegDir 指定的目录不满足要求（需要同时有 ffmpeg.exe / ffplay.exe / ffprobe.exe）: " + $Explicit)
    }
    $cands = New-Object System.Collections.ArrayList
    [void]$cands.Add((Join-Path $Root 'third_party\ffmpeg'))
    [void]$cands.Add($resBin)
    foreach ($dl in @((Join-Path $env:USERPROFILE 'Downloads'), $env:USERPROFILE)) {
        if (-not (Test-Path $dl)) { continue }
        foreach ($d in (Get-ChildItem $dl -Directory -Filter 'ffmpeg*' -ErrorAction SilentlyContinue)) {
            [void]$cands.Add((Join-Path $d.FullName 'bin'))
            [void]$cands.Add($d.FullName)
        }
    }
    [void]$cands.Add('C:\ffmpeg\bin')
    $onPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($onPath) { [void]$cands.Add((Split-Path -Parent $onPath.Source)) }
    foreach ($c in $cands) { if (Test-FfmpegDir $c) { return (Resolve-Path $c).Path } }
    return $null
}

$ffDir = Find-Ffmpeg -Explicit $FfmpegDir
if (-not $ffDir) {
    Die @'
找不到 ffmpeg。请任选一种方式提供（脚本会自动搜索，无需手工复制）：
  a) 从 https://www.gyan.dev/ffmpeg/builds/ 或 https://github.com/BtbN/FFmpeg-Builds/releases 下载 Windows 构建，
     解压到 third_party\ffmpeg\（使该目录下直接有 ffmpeg.exe / ffplay.exe / ffprobe.exe）；
  b) 放到 %USERPROFILE%\Downloads\ffmpeg\bin\ 下（脚本会自动找到）；
  c) 用 -FfmpegDir <目录> 指定。
需要同时包含 ffmpeg.exe、ffplay.exe、ffprobe.exe（ffplay 是打铃与喊话的播放器，必须有）。
'@
}
$ffFiles = @(Get-ChildItem $ffDir -File | Where-Object { $_.Extension -in '.exe', '.dll' })
Ok ("ffmpeg 目录: " + $ffDir + "（" + $ffFiles.Count + " 个 exe/dll）")

# --- 合规：判定 ffmpeg 的许可类型
$ffExe = Join-Path $ffDir 'ffmpeg.exe'
$ffVersion = ''
$ffConfig = ''
try {
    $ffOut = & $ffExe -version 2>&1
    $ffVersion = ($ffOut | Select-Object -First 1)
    $ffConfig = (($ffOut | Select-String -Pattern '^\s*configuration:' | Select-Object -First 1) -replace '^\s*configuration:\s*', '')
} catch { Warn '无法执行 ffmpeg -version，许可判定跳过' }

$ffLicense = 'LGPL-2.1-or-later'
if ($ffConfig -match '--enable-nonfree') { $ffLicense = 'nonfree（不可再分发！）' }
elseif ($ffConfig -match '--enable-gpl') { $ffLicense = 'GPL-2.0-or-later' }
Info ("版本: " + $ffVersion)
Info ("判定许可: " + $ffLicense)
if ($ffLicense -like 'nonfree*') {
    Warn 'ffmpeg 是 --enable-nonfree 构建：**不允许再分发**。请换用官方/GPL/LGPL 构建再打包。'
} elseif ($ffLicense -like 'GPL*') {
    Warn 'ffmpeg 是 GPL 构建：随安装包分发时，整个分发物需满足 GPL 的要求（本脚本已放入其 LICENSE 与声明）。'
} else {
    Ok 'ffmpeg 是 LGPL 构建：以独立可执行文件方式随包分发、并附许可与可替换声明即可。'
}
} else {
    Step '3/6' '定位 ffmpeg'
    Info '仅构建安卓端：跳过 ffmpeg（它是电脑端依赖，且按合规要求不进 git）'
    $ffDir = ''; $ffLicense = ''
}

# ================================================================ 4. 铃声
Step '4/6' '准备初始铃声'

# 铃声只给电脑端安装包用；安卓端（-AndroidOnly）整段跳过 —— APK 不需要铃声，
# 而合成铃声需要 ffmpeg，安卓端又刻意不取 ffmpeg，两者会互相打架（CI 上踩过）。
if (-not $AndroidOnly) {
$ringSrc = $null
if (-not $MinimalRing) {
    if ($RingDir) { if (Test-Path $RingDir) { $ringSrc = (Resolve-Path $RingDir).Path } else { Warn ("-RingDir 不存在: " + $RingDir) } }
    if (-not $ringSrc) {
        foreach ($c in @((Join-Path $Root 'third_party\ring'), $resRing)) {
            if ((Test-Path $c) -and @(Get-ChildItem $c -File -Filter '*.mp3').Count -gt 0) { $ringSrc = $c; break }
        }
    }
}

$ringStage = Join-Path $assets 'ring'
if (Test-Path $ringStage) { Remove-Item $ringStage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ringStage | Out-Null

if ($ringSrc) {
    # 只取音频文件：第三方目录里通常还放着 README / 许可说明，不该被释放到用户的铃声目录里
    $audio = @(Get-ChildItem $ringSrc -File | Where-Object { $_.Extension -match '^\.(mp3|wav|m4a|aac|ogg|flac|wma)$' })
    if ($audio.Count -eq 0) { Die ("铃声目录里没有音频文件: " + $ringSrc) }
    foreach ($a in $audio) { Copy-Item $a.FullName $ringStage -Force }
    Ok ("使用现有铃声: " + $ringSrc + "（" + $audio.Count + " 个音频文件）")
} else {
    # 没有现成铃声：用 ffmpeg **纯合成**开源铃声（无版权），不再生成静音占位。
    # 生成 6 个：上课铃/下课铃/集合铃/正式铃（纯铃）+ 预备铃/下课铃_语音版（带 SAPI 语音提示）。
    $mb = Join-Path $Root 'tools\make-bells.ps1'
    if (-not (Test-Path $mb)) { Die ('找不到 ' + $mb) }
    if (-not $ffDir) { Die '合成铃声需要 ffmpeg：请先跑 tools\fetch-deps.ps1，或用 -FfmpegDir 指定' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $mb -Root $Root -FfmpegDir $ffDir -OutDir $ringStage -UpdateTemplate -Force
    if ($LASTEXITCODE -ne 0) { Die ('合成铃声失败（退出码 ' + $LASTEXITCODE + '）') }
    $made = @(Get-ChildItem $ringStage -File -Filter '*.mp3').Count
    if ($made -eq 0) { Die '合成铃声失败：输出目录为空' }
    Ok ('已用 ffmpeg 合成 ' + $made + ' 个开源铃声（无版权，可安全分发）')
    [void]$script:Notes.Add('初始铃声为 ffmpeg 纯合成的开源铃声（无第三方音频）')
}
[void]$script:Notes.Add(("初始铃声来源: " + $(if ($ringSrc) { $ringSrc } else { '自动生成的静音占位' })))
} else {
    Step '4/6' '准备初始铃声'
    Info '仅构建安卓端：跳过铃声准备（APK 不需要铃声）'
}

# ================================================================ 5. 电脑端
$desktopOk = $false
if (-not $AndroidOnly) {
    Step '5/6' '构建电脑端'

    # --- 发布（带图标覆盖，不动工程里的真实图标）
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
    $pubArgs = @($proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                 '-p:PublishSingleFile=true', '-o', $pub, '--nologo',
                 ('-p:ApplicationIcon=' + (Resolve-Path $IconFile).Path))
    Info '正在发布（自包含单文件）...'
    & dotnet publish @pubArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { Die ("dotnet publish 失败（退出码 " + $LASTEXITCODE + "）") }
    if (-not (Test-Path (Join-Path $pub 'BroadcastSystemNet.exe'))) { Die '发布产物里没有 BroadcastSystemNet.exe' }
    Ok '发布完成'

    # --- 把 ffmpeg 直接放进 publish（不污染源码目录 res\bin）
    $pubBin = Join-Path $pub 'res\bin'
    New-Item -ItemType Directory -Force -Path $pubBin | Out-Null
    # 只复制可执行文件与 DLL：许可与说明另有 licenses\ 与"第三方组件声明.txt"，
    # 不要把 ffmpeg 目录里的 README/LICENSE 混进 res\bin（那是给用户看的运行时目录）
    $ffPayload = @(Get-ChildItem $ffDir -File | Where-Object { $_.Extension -in '.exe', '.dll' })
    if ($ffPayload.Count -eq 0) { Die ("ffmpeg 目录里没有 exe/dll: " + $ffDir) }
    foreach ($f in $ffPayload) { Copy-Item $f.FullName $pubBin -Force }
    Ok ("已把 ffmpeg 放入发布产物（" + $ffPayload.Count + " 个 exe/dll）")

    # --- 把铃声放进 publish（.iss 有编译期校验，空目录会拒绝编译）
    $pubRing = Join-Path $pub 'res\ring'
    New-Item -ItemType Directory -Force -Path $pubRing | Out-Null
    Copy-Item (Join-Path $ringStage '*') $pubRing -Force
    Ok ("已把初始铃声放入发布产物（" + @(Get-ChildItem $pubRing -File).Count + " 个文件）")

    # 【图标版权】发布产物里的站点图标换成占位图。
    # res\favicon.ico 未入库（.gitignore 有 *.ico），但开发机上往往有真图标，
    # 而脚本对它是"存在就保留" -> 打出来的开源包里带的就是真图标（可能有版权）。
    # 只覆盖 publish\res\favicon.ico，源码树不动，所以不需要备份/恢复。
    if (Test-Path (Join-Path $pub 'res')) {
        New-BlankIco -Path (Join-Path $pub 'res\favicon.ico')
        Ok '已把发布产物里的 res\favicon.ico 换成占位图（避免带上可能有版权的真图标）'
    }

    # --- 合规：许可文件 + 第三方组件声明
    if (-not $NoNotice) {
        $licDir = Join-Path $pub 'licenses'
        New-Item -ItemType Directory -Force -Path $licDir | Out-Null
        $copiedLic = @()
        foreach ($lf in (Get-ChildItem $ffDir -File -ErrorAction SilentlyContinue |
                         Where-Object { $_.Name -match '^(LICENSE|COPYING|README)' })) {
            Copy-Item $lf.FullName (Join-Path $licDir ('ffmpeg-' + $lf.Name)) -Force
            $copiedLic += $lf.Name
        }
        $notice = @()
        $notice += '智能校园广播系统 · 第三方组件声明'
        $notice += ('生成时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
        $notice += ''
        $notice += '本程序随包分发以下第三方组件，各自遵循其原始许可：'
        $notice += ''
        $notice += '1) FFmpeg（ffmpeg / ffplay / ffprobe 及其 DLL）'
        $notice += ('   版本    : ' + $ffVersion)
        $notice += ('   许可判定: ' + $ffLicense)
        $notice += ('   构建配置: ' + $ffConfig)
        $notice += '   源码获取: https://ffmpeg.org/download.html'
        $notice += '   说明    : 本程序以**独立进程**方式调用 ffmpeg/ffplay（不静态链接），'
        $notice += '             因此 LGPL 构建下可随包分发；使用者可自行替换 res\bin 下的同名文件。'
        if ($copiedLic.Count -gt 0) { $notice += ('   随附许可: licenses\ffmpeg-*（' + ($copiedLic -join ', ') + '）') }
        $notice += ''
        $notice += '2) BouncyCastle.Cryptography   许可: MIT'
        $notice += '3) Font Awesome Free           许可: 字体 SIL OFL 1.1；图标 CC BY 4.0'
        $notice += '4) Chart.js                    许可: MIT'
        $notice += '5) flatpickr                   许可: MIT'
        $notice += '6) MIUIX                       许可: Apache-2.0'
        $notice += '7) CameraX / ML Kit / ZXing    许可: Apache-2.0'
        $notice += '8) vnt / vnts（组网协议参考实现）许可: Apache-2.0'
        $notice += ''
        $notice += '本项目自身尚未声明开源许可。'
        [System.IO.File]::WriteAllLines((Join-Path $pub '第三方组件声明.txt'), $notice, (New-Object System.Text.UTF8Encoding($true)))
        Ok ('已生成第三方组件声明' + $(if ($copiedLic.Count -gt 0) { '，并附上 ffmpeg 的 ' + ($copiedLic -join '/') } else { '（ffmpeg 目录里未找到 LICENSE 文件）' }))
    }

    # --- 复用 build-release.ps1 的门禁 + 产物核对 + 离线自检 + 安装包编译
    $br = Join-Path $Root 'tools\build-release.ps1'
    if (-not (Test-Path $br)) { Die ("找不到 " + $br) }
    $brArgs = @('-Root', $Root, '-SkipPublish')
    # 开源包用独立文件名，避免和闭源包（BroadcastSystem_Setup_v{版本}.exe）互相覆盖
    $brArgs += @('-OutputBaseSuffix', '_opensource')
    if ($SkipInstaller) { $brArgs += '-SkipInstaller' }
    if ($SkipSelfTest)  { $brArgs += '-SkipSelfTest' }
    Info '调用 build-release.ps1 -SkipPublish（静态门禁 + 产物核对 + 离线自检 + 编译安装包）...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $br @brArgs
    if ($LASTEXITCODE -ne 0) { Die ("build-release.ps1 未通过（退出码 " + $LASTEXITCODE + "）") }
    $desktopOk = $true
}

# ================================================================ 6. 安卓端
$androidOk = $false
if ($needAndroid) {
    Step '6/6' '构建安卓端'

    if (-not $androidReady) {
        Warn '安卓工具链不完整，跳过 APK 构建（详见上方第 1 步的提示）'
    } else {
        # local.properties：Gradle 需要知道 SDK 位置（该文件不入库）
        $lp = Join-Path $androidDir 'local.properties'
        if (-not (Test-Path $lp)) {
            $sdkEsc = $AndroidSdk -replace '\\', '\\'
            [System.IO.File]::WriteAllText($lp, ("sdk.dir=" + $sdkEsc + "`n"), (New-Object System.Text.UTF8Encoding($false)))
            Ok '已生成 android\local.properties'
        } else { Info 'android\local.properties 已存在，保留不动' }

        # 安卓启动图标（mipmap-* 未入库；缺了 Gradle 会因 @mipmap/ic_launcher 找不到而失败）
        $resRoot = Join-Path $androidDir 'app\src\main\res'
        $needIcons = $ForcePlaceholderIcons -or (-not (Test-Path (Join-Path $resRoot 'mipmap-xhdpi\ic_launcher.png')))
        # 【备份】强制占位且本机已有真图标时，先把它们备份出来，构建完在 finally 里恢复 ——
        # 这样既保证开源包用的是占位图，又不会把工作区里的真图标覆盖掉。
        $iconBackup = $null
        $iconDirs = @('mipmap-mdpi','mipmap-hdpi','mipmap-xhdpi','mipmap-xxhdpi','mipmap-xxxhdpi','mipmap-anydpi-v26','values')
        if ($ForcePlaceholderIcons -and (Test-Path (Join-Path $resRoot 'mipmap-xhdpi\ic_launcher.png'))) {
            $iconBackup = Join-Path $env:TEMP ('cbs-icons-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            New-Item -ItemType Directory -Force -Path $iconBackup | Out-Null
            foreach ($d in $iconDirs) {
                $src = Join-Path $resRoot $d
                if (Test-Path $src) { Copy-Item $src (Join-Path $iconBackup $d) -Recurse -Force }
            }
            Ok ('已备份原有安卓图标到 ' + $iconBackup)
            [void]$script:Notes.Add('安卓图标为脚本生成的占位图（真图标已备份并在构建后恢复）')
        }
        if ($needIcons) {
            Add-Type -AssemblyName System.Drawing
            $bg = [System.Drawing.Color]::FromArgb(255, 0x8F, 0xD8, 0xFB)   # 与 values\ic_launcher_background.xml 一致
            $dens = @{ 'mdpi' = 1.0; 'hdpi' = 1.5; 'xhdpi' = 2.0; 'xxhdpi' = 3.0; 'xxxhdpi' = 4.0 }
            foreach ($d in $dens.Keys) {
                $dir = Join-Path $resRoot ('mipmap-' + $d)
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                $launcherPx = [int][Math]::Round(48 * $dens[$d])
                $fgPx       = [int][Math]::Round(108 * $dens[$d])
                # 老系统整图：浅蓝圆角方块
                $bmp = New-Object System.Drawing.Bitmap($launcherPx, $launcherPx)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
                $brush = New-Object System.Drawing.SolidBrush($bg)
                $g.FillEllipse($brush, 0, 0, $launcherPx - 1, $launcherPx - 1)
                $bmp.Save((Join-Path $dir 'ic_launcher_round.png'), [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.FillRectangle($brush, 0, 0, $launcherPx, $launcherPx)
                $bmp.Save((Join-Path $dir 'ic_launcher.png'), [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose(); $brush.Dispose()
                # 自适应前景 + 单色层：透明底 + 居中白色圆
                foreach ($pair in @(@('ic_launcher_foreground.png', $bg), @('ic_launcher_monochrome.png', [System.Drawing.Color]::White))) {
                    $fb = New-Object System.Drawing.Bitmap($fgPx, $fgPx)
                    $fg = [System.Drawing.Graphics]::FromImage($fb)
                    $fg.SmoothingMode = 'AntiAlias'; $fg.Clear([System.Drawing.Color]::Transparent)
                    $br2 = New-Object System.Drawing.SolidBrush($pair[1])
                    $r = [int]($fgPx * 0.30)
                    $fg.FillEllipse($br2, [int](($fgPx - $r) / 2), [int](($fgPx - $r) / 2), $r, $r)
                    $fb.Save((Join-Path $dir $pair[0]), [System.Drawing.Imaging.ImageFormat]::Png)
                    $fg.Dispose(); $fb.Dispose(); $br2.Dispose()
                }
            }
            # 自适应图标 XML（mipmap-anydpi-v26 也在忽略清单里）
            $anydpi = Join-Path $resRoot 'mipmap-anydpi-v26'
            New-Item -ItemType Directory -Force -Path $anydpi | Out-Null
            $xml = '<?xml version="1.0" encoding="utf-8"?>' + "`n" +
                   '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">' + "`n" +
                   '    <background android:drawable="@color/ic_launcher_background" />' + "`n" +
                   '    <foreground android:drawable="@mipmap/ic_launcher_foreground" />' + "`n" +
                   '    <monochrome android:drawable="@mipmap/ic_launcher_monochrome" />' + "`n" +
                   '</adaptive-icon>' + "`n"
            foreach ($n in @('ic_launcher.xml', 'ic_launcher_round.xml')) {
                [System.IO.File]::WriteAllText((Join-Path $anydpi $n), $xml, (New-Object System.Text.UTF8Encoding($false)))
            }
            $vals = Join-Path $resRoot 'values'
            New-Item -ItemType Directory -Force -Path $vals | Out-Null
            $bgXml = Join-Path $vals 'ic_launcher_background.xml'
            if (-not (Test-Path $bgXml)) {
                [System.IO.File]::WriteAllText($bgXml,
                    ('<?xml version="1.0" encoding="utf-8"?>' + "`n" + '<resources>' + "`n" +
                     '    <color name="ic_launcher_background">#8FD8FB</color>' + "`n" + '</resources>' + "`n"),
                    (New-Object System.Text.UTF8Encoding($false)))
            }
            Ok '已生成安卓占位启动图标（5 个密度 + 自适应 XML）'
            [void]$script:Notes.Add('安卓启动图标为脚本生成的占位图（mipmap-* 未入库）')
        } else { Info '安卓启动图标已存在，保留不动' }

        $env:JAVA_HOME = $JavaHome
        $env:GRADLE_USER_HOME = Join-Path $Root '.gradle-home'
        New-Item -ItemType Directory -Force -Path $env:GRADLE_USER_HOME | Out-Null
        New-Item -ItemType Directory -Force -Path $distDir | Out-Null

        # --- 发布签名：参数优先，其次环境变量；都没有则回退 debug 签名（并醒目提示）
        if (-not $Keystore)   { $Keystore   = $env:CBS_KEYSTORE }
        if (-not $KeyAlias)   { $KeyAlias   = $env:CBS_KEY_ALIAS }
        if (-not $StorePass)  { $StorePass  = $env:CBS_KEYSTORE_PASSWORD }
        if (-not $KeyPass)    { $KeyPass    = $env:CBS_KEY_PASSWORD }
        if ($Keystore) {
            if (-not (Test-Path $Keystore)) { Die ("签名密钥库不存在: " + $Keystore) }
            if (-not $KeyAlias) { Die '提供了密钥库但没有别名（-KeyAlias 或 CBS_KEY_ALIAS）' }
            $env:CBS_KEYSTORE          = (Resolve-Path $Keystore).Path
            $env:CBS_KEY_ALIAS         = $KeyAlias
            $env:CBS_KEYSTORE_PASSWORD = $StorePass
            $env:CBS_KEY_PASSWORD      = $KeyPass
            Ok ("安卓发布签名: " + $env:CBS_KEYSTORE + "  alias=" + $KeyAlias)
            [void]$script:Notes.Add('安卓 APK 使用发布密钥库签名（来自参数/环境变量，未写入仓库）')
        } else {
            Warn ('未提供发布签名密钥，APK 将使用 **debug 签名**：不能上架应用商店，' +
                  '且不同机器构建出的 APK 无法互相覆盖安装（只能卸载重装，会清掉 App 数据）。' +
                  '要用发布签名，请设 CBS_KEYSTORE / CBS_KEY_ALIAS / CBS_KEYSTORE_PASSWORD / CBS_KEY_PASSWORD，' +
                  '或用 -Keystore / -KeyAlias / -StorePass / -KeyPass 传入。')
        }

        $apkSrc = Join-Path $androidDir 'app\build\outputs\apk\release\app-release.apk'
        if (Test-Path $apkSrc) { Remove-Item $apkSrc -Force }

        Info ('正在执行 Gradle :app:assembleRelease（首次构建会下载依赖，可能较久）...')
        Push-Location $androidDir
        try {
            & $GradleBin ':app:assembleRelease' '--console=plain' '--no-daemon'
            $grc = $LASTEXITCODE
        } finally {
            Pop-Location
            # 【恢复】把备份的真图标原样放回（构建失败也会执行到这里）
            if ($iconBackup) {
                foreach ($d in $iconDirs) {
                    $src = Join-Path $iconBackup $d
                    if (Test-Path $src) {
                        $dst = Join-Path $resRoot $d
                        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
                        Copy-Item $src $dst -Recurse -Force
                    }
                }
                Ok '已恢复原有安卓图标（工作区未被占位图覆盖）'
            }
        }
        if ($grc -ne 0) { Die ("Gradle 构建失败（退出码 " + $grc + "）") }
        if (-not (Test-Path $apkSrc)) { Die ("没找到 APK 产物: " + $apkSrc) }
        # 开源 APK 用独立文件名，避免覆盖闭源包（dist\BroadcastApp.apk）
        $apkOut = Join-Path $distDir 'BroadcastApp_opensource.apk'
        Copy-Item $apkSrc $apkOut -Force
        $apkSize = [math]::Round((Get-Item $apkOut).Length / 1MB, 2)
        Ok ('APK: dist\BroadcastApp_opensource.apk（' + $apkSize + ' MB）')
        $androidOk = $true
    }
}

# ================================================================ 汇总
Write-Host ''
Write-Host '############################################################' -ForegroundColor DarkCyan
Write-Host '#  构建结果' -ForegroundColor DarkCyan
Write-Host '############################################################' -ForegroundColor DarkCyan
Write-Host ('  电脑端 publish : ' + $(if ($desktopOk) { 'OK  ' + $pub } else { '未构建' }))
if (-not $SkipInstaller) {
    $setup = Get-ChildItem (Join-Path $Root 'Output') -Filter '*.exe' -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host ('  安装包         : ' + $(if ($setup) { 'OK  ' + $setup.FullName + '（' + [math]::Round($setup.Length / 1MB, 2) + ' MB）' } else { '未产出' }))
}
Write-Host ('  安卓 APK       : ' + $(if ($androidOk) { 'OK  dist\BroadcastApp_opensource.apk' } else { '未构建' }))
Write-Host ''
Write-Host '  本次使用的输入：'
Write-Host ('    ffmpeg   : ' + $ffDir + '   许可: ' + $ffLicense)
Write-Host ('    铃声     : ' + $(if ($ringSrc) { $ringSrc } else { '自动生成的静音占位铃声' }))
Write-Host ('    程序图标 : ' + $IconFile)
Write-Host ''
if ($script:Warnings.Count -gt 0) {
    Write-Host '  需要注意（' + $script:Warnings.Count + ' 条）：' -ForegroundColor Yellow
    foreach ($w in $script:Warnings) { Write-Host ('    - ' + $w) -ForegroundColor Yellow }
    Write-Host ''
}
Write-Host '  提示：ffmpeg 与第三方铃声都**没有**写进 git，只存在于发布产物里。' -ForegroundColor DarkGray
Write-Host '        要清理本次生成的占位资源，删除 tools\opensource\ 与 dist\ 即可。' -ForegroundColor DarkGray
Write-Host ''
exit 0
