# 智能校园广播系统 · 用 ffmpeg 合成开源铃声（无版权问题）
# ==========================================================================
# 为什么需要：GitHub 上没有可用的中国中小学铃声库（版权原因），而"全新克隆 + 没有音乐库"
# 的人需要一个**能真的响**的默认铃声。这里用 ffmpeg 纯合成：
#   sine 基频 + 泛音 -> 指数衰减(afade curve=exp) -> 回声混响(aecho) -> 多次敲击
# 语音提示用 Windows SAPI（本机离线中文语音）；没有语音包时自动退化为纯铃声并提示。
#
# 生成（默认输出到 third_party\ring）：
#   上课铃.mp3          纯铃声（3 响，880Hz）
#   下课铃.mp3          纯铃声（2 响，660Hz）
#   集合铃.mp3          纯铃声（4 响，523Hz）
#   预备铃.mp3          上课铃声 + 语音"请各班同学做好准备，上课铃声即将响起"
#   正式铃.mp3          纯铃声（与上课铃同源，单独一份便于模板引用）
#   下课铃_语音版.mp3    下课铃声 + 语音"下课时间到了，请同学们注意安全"
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\tools\make-bells.ps1
#   ... -UpdateTemplate          同时重写 res\config\music.jsonl 模板（让新装机器直接看到这些铃声）
#   ... -FfmpegDir D:\ffmpeg\bin 指定 ffmpeg
#   ... -Force                   已存在也重生成
#
# 注意：本文件必须保存为 UTF-8 带 BOM（build-release.ps1 的脚本自检会检查）。

param(
    [string]$Root = '',
    [string]$FfmpegDir = '',
    [string]$OutDir = '',
    [switch]$UpdateTemplate,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $OutDir) { $OutDir = Join-Path $Root 'third_party\ring' }

# ---------- 定位 ffmpeg ----------
if (-not $FfmpegDir) {
    foreach ($c in @((Join-Path $Root 'third_party\ffmpeg'), (Join-Path $Root 'BroadcastSystemNet\res\bin'))) {
        if (Test-Path (Join-Path $c 'ffmpeg.exe')) { $FfmpegDir = $c; break }
    }
}
if (-not $FfmpegDir) { throw '找不到 ffmpeg：请先跑 tools\fetch-deps.ps1，或用 -FfmpegDir 指定' }
$ff = Join-Path $FfmpegDir 'ffmpeg.exe'
if (-not (Test-Path $ff)) { throw "找不到 $ff" }

# 本机安全软件会拦 ffmpeg 往仓库目录写文件，所以统一先生成到临时目录，最后复制过去
# 临时目录放在**仓库内**：实测本机安全软件/沙箱会拦 ffmpeg 往 %TEMP% 写（Permission denied, exit -13），
# 但仓库目录内可写。用完即删（run_bells 已在 .gitignore 的 run_* 规则里）。
$tmp = Join-Path $Root ('run_bells\' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Run-Ff([string[]]$FfArgs) {
    # 注意：参数名不能叫 $Args —— 那是 PowerShell 的自动变量，会被覆盖成空
    $out = & $ff -hide_banner -loglevel error -y @FfArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ('ffmpeg 失败（退出码 ' + $LASTEXITCODE + '）：' + "`n  " + (($out | Out-String).Trim()) + "`n  参数: " + ($FfArgs -join ' '))
    }
}

# ---------- 合成一次"钟声"（单次敲击）----------
function New-Strike([int]$freq, [double]$sec, [string]$wav) {
    $f1 = 'sine=frequency=' + $freq + ':duration=' + $sec
    $f2 = 'sine=frequency=' + ($freq * 2) + ':duration=' + $sec
    $f3 = 'sine=frequency=' + [int]($freq * 2.76) + ':duration=' + $sec   # 非整数倍泛音，更像金属钟
    # 指数衰减 + 短回声（模拟空间混响），泛音音量递减
    $fc = '[0:a]volume=1.0[a0];[1:a]volume=0.30[a1];[2:a]volume=0.15[a2];' +
          '[a0][a1][a2]amix=inputs=3:duration=longest:normalize=0,' +
          'afade=t=out:st=0:d=' + $sec + ':curve=exp,' +
          'aecho=0.8:0.72:55|130:0.32|0.18,volume=1.5[out]'
    Run-Ff @('-f','lavfi','-i',$f1,'-f','lavfi','-i',$f2,'-f','lavfi','-i',$f3,
              '-filter_complex',$fc,'-map','[out]','-ac','1','-ar','44100',$wav)
}

# ---------- 合成 N 次敲击 ----------
function New-Chime([int]$freq, [int]$strikes, [double]$strikeSec, [double]$gap, [string]$wav) {
    $one = Join-Path $tmp 'strike.wav'
    New-Strike $freq $strikeSec $one
    if ($strikes -le 1) { Copy-Item $one $wav -Force; return }
    $parts = @(); $mix = @()
    for ($i = 0; $i -lt $strikes; $i++) { $parts += ('[s' + $i + ']'); $mix += ('[d' + $i + ']') }
    $fc = '[0:a]asplit=' + $strikes + [string]::Join('', $parts) + ';'
    for ($i = 0; $i -lt $strikes; $i++) {
        $delayMs = [int](($strikeSec + $gap) * 1000 * $i)
        if ($delayMs -eq 0) { $fc += ('[s' + $i + ']anull[d' + $i + '];') }
        else { $fc += ('[s' + $i + ']adelay=' + $delayMs + '|' + $delayMs + '[d' + $i + '];') }
    }
    $fc += [string]::Join('', $mix) + 'amix=inputs=' + $strikes + ':duration=longest:normalize=0[out]'
    Run-Ff @('-i',$one,'-filter_complex',$fc,'-map','[out]','-ac','1','-ar','44100',$wav)
}

# ---------- 语音提示（SAPI；失败则返回 $null）----------
function New-Speech([string]$text, [string]$wav) {
    try {
        Add-Type -AssemblyName System.Speech
        $sp = New-Object System.Speech.Synthesis.SpeechSynthesizer
        try {
            $zh = $sp.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -like 'zh*' } | Select-Object -First 1
            if ($zh) { $sp.SelectVoice($zh.VoiceInfo.Name) }
            $sp.Rate = 0
            $sp.SetOutputToWaveFile($wav)
            $sp.Speak($text)
        } finally { $sp.Dispose() }
        if ((Test-Path $wav) -and (Get-Item $wav).Length -gt 1024) { return $wav }
        return $null
    } catch { return $null }
}

# ---------- 拼：铃声 + 语音 ----------
function Join-BellAndSpeech([string]$bellWav, [string]$speechWav, [string]$outMp3) {
    Run-Ff @('-i',$bellWav,'-i',$speechWav,
              '-filter_complex','[0:a][1:a]concat=n=2:v=0:a=1[out]','-map','[out]',
              '-ac','1','-ar','44100','-c:a','libmp3lame','-b:a','128k',$outMp3)
}

function To-Mp3([string]$wav, [string]$mp3) {
    Run-Ff @('-i',$wav,'-ac','1','-ar','44100','-c:a','libmp3lame','-b:a','128k',$mp3)
}

# ---------- 生成 ----------
$bells = @(
    @{ File = '上课铃.mp3';        Freq = 880; Strikes = 3; StrikeSec = 1.9; Gap = 0.55; Text = '' },
    @{ File = '下课铃.mp3';        Freq = 660; Strikes = 2; StrikeSec = 2.2; Gap = 0.70; Text = '' },
    @{ File = '集合铃.mp3';        Freq = 523; Strikes = 4; StrikeSec = 1.3; Gap = 0.35; Text = '' },
    @{ File = '预备铃.mp3';        Freq = 880; Strikes = 3; StrikeSec = 1.9; Gap = 0.55; Text = '请各班同学做好准备，上课铃声即将响起' },
    @{ File = '正式铃.mp3';        Freq = 880; Strikes = 3; StrikeSec = 1.9; Gap = 0.55; Text = '' },
    @{ File = '下课铃_语音版.mp3'; Freq = 660; Strikes = 2; StrikeSec = 2.2; Gap = 0.70; Text = '下课时间到了，请同学们注意安全' }
)

$made = @(); $speechOk = $true
foreach ($b in $bells) {
    $target = Join-Path $OutDir $b.File
    if ((Test-Path $target) -and -not $Force) { Write-Host ('  [跳过] ' + $b.File + '（已存在）'); continue }

    $bellWav = Join-Path $tmp ('bell-' + $b.Freq + '-' + $b.Strikes + '.wav')
    if (-not (Test-Path $bellWav)) { New-Chime $b.Freq $b.Strikes $b.StrikeSec $b.Gap $bellWav }

    $tmpMp3 = Join-Path $tmp ('out-' + [guid]::NewGuid().ToString('N').Substring(0,6) + '.mp3')
    if ($b.Text) {
        $spWav = Join-Path $tmp ('tts-' + [guid]::NewGuid().ToString('N').Substring(0,6) + '.wav')
        $sp = New-Speech $b.Text $spWav
        if ($sp) { Join-BellAndSpeech $bellWav $sp $tmpMp3 }
        else {
            $speechOk = $false
            Write-Host ('  [提示] 本机语音合成不可用，' + $b.File + ' 退化为纯铃声')
            To-Mp3 $bellWav $tmpMp3
        }
    } else {
        To-Mp3 $bellWav $tmpMp3
    }
    Copy-Item $tmpMp3 $target -Force
    $made += $b.File
    Write-Host ('  [OK] ' + $b.File + '（' + [math]::Round((Get-Item $target).Length / 1KB, 1) + ' KB）')
}

# ---------- 可选：重写音乐库模板 ----------
if ($UpdateTemplate) {
    $tpl = Join-Path $Root 'BroadcastSystemNet\res\config\music.jsonl'
    $lines = New-Object System.Collections.ArrayList
    $id = 1
    foreach ($b in $bells) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($b.File)
        [void]$lines.Add('{"id":"' + $id + '","name":"' + $name + '","path":"ring/' + $b.File + '","type":"file"}')
        $id++
    }
    [void]$lines.Add('{"id":"' + $id + '","name":"中国之声","path":"https://ngcdn001.cnr.cn/live/zgzs/index.m3u8","type":"hls"}')
    [System.IO.File]::WriteAllText($tpl, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ('  [OK] 已重写音乐库模板：' + $tpl + '（' + $lines.Count + ' 条）')
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host ('完成：本次生成/更新 ' + $made.Count + ' 个铃声，输出目录 ' + $OutDir) -ForegroundColor Green
if (-not $speechOk) { Write-Host '注意：语音提示未能生成（本机无中文语音包或被安全软件拦截），带语音的那几个退化为纯铃声。' -ForegroundColor Yellow }