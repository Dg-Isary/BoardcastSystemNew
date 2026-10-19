param(
    [string]$Root = 'D:\USER\Desktop\BroadcastSystemNet'
)

# 构建产物清理 + 回到正常输出目录（run）。
# 背景：火绒的实时防护会按住 run\bin\Debug\net8.0-windows\BroadcastSystemNet.dll 这类文件的写/删句柄
#       （能读、不能写，报 MSB3021 Access denied，连"重启后删除"登记都被拒）。
#       白名单生效或重启一次之后，跑本脚本即可把所有临时构建目录清掉并回到正常的 run 目录。

$ErrorActionPreference = 'Continue'

Write-Host '=== 1) 退出正在运行的程序 ===' -ForegroundColor Cyan
Get-Process -Name BroadcastSystemNet -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ('  结束进程 ' + $_.Id)
    try { $_.Kill(); $_.WaitForExit(5000) | Out-Null } catch { }
}
Start-Sleep -Seconds 2

Write-Host '=== 2) 删除临时/残留构建目录 ===' -ForegroundColor Cyan
$dirs = @(
    'BroadcastSystemNet\run',
    'run_alt',
    'run_alt_locked',
    'run_locked_backup',
    '.build2',
    '.build2_locked',
    '.build3',
    '.buildcheck'
)
$blocked = @()
foreach ($d in $dirs) {
    $p = Join-Path $Root $d
    if (-not (Test-Path $p)) { continue }
    try {
        Remove-Item $p -Recurse -Force -ErrorAction Stop
        Write-Host ('  已删除 ' + $d) -ForegroundColor Green
    } catch {
        $blocked += $d
        Write-Host ('  删除失败 ' + $d + ' -> ' + $_.Exception.Message) -ForegroundColor Yellow
    }
}

Write-Host '=== 3) 重新构建到 run ===' -ForegroundColor Cyan
& dotnet build (Join-Path $Root 'BroadcastSystemNet\BroadcastSystemNet.csproj') -v:m --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host '构建失败：上面的错误如果是 MSB3021 Access denied，说明文件仍被杀软按住，重启后再跑一次本脚本。' -ForegroundColor Red
    exit 1
}

$exe = Join-Path $Root 'BroadcastSystemNet\run\bin\Debug\net8.0-windows\BroadcastSystemNet.exe'
Write-Host ('=== 完成：' + $exe) -ForegroundColor Green
if ($blocked.Count -gt 0) { Write-Host ('仍被占用（重启后再跑本脚本即可清掉）：' + ($blocked -join '、')) -ForegroundColor Yellow }
Write-Host '=== 4) 离线自检 ===' -ForegroundColor Cyan
& $exe --self-test | Select-Object -Last 2