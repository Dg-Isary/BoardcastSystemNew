param(
    [string]$Root = 'D:\USER\Desktop\BroadcastSystemNet'
)

# 前端脚本语法检查（需要 node）。
# 背景：index.html 里是内联 JS，一旦有语法错误（例如把注释插到代码中间、漏了闭合括号），
#       dotnet build 依然成功、后端接口也全绿，但整个看板会静默失效——只有浏览器里才看得出来。
#       因此每次改完 res\index.html / res\mobile.html 都必须跑一次本脚本。

$ErrorActionPreference = 'Stop'
$files = @(
    (Join-Path $Root 'BroadcastSystemNet\res\index.html'),
    (Join-Path $Root 'BroadcastSystemNet\res\mobile.html')
)

$tmpDir = Join-Path $env:TEMP ('jscheck_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$fail = 0
$total = 0

try {
    foreach ($file in $files) {
        if (-not (Test-Path $file)) { Write-Host ("跳过（不存在）: " + $file) -ForegroundColor Yellow; continue }
        $name = Split-Path $file -Leaf
        $html = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
        $pos = 0; $idx = 0
        while ($true) {
            $s = $html.IndexOf('<script>', $pos)
            if ($s -lt 0) { break }
            $e = $html.IndexOf('</script>', $s)
            if ($e -lt 0) { break }
            $body = $html.Substring($s + 8, $e - $s - 8)
            $idx++
            $total++
            $jsPath = Join-Path $tmpDir ("{0}_block{1}.js" -f $name, $idx)
            [System.IO.File]::WriteAllText($jsPath, $body, (New-Object System.Text.UTF8Encoding($false)))
            $out = & node --check $jsPath 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                $fail++
                $line = (($out -split "`r?`n") | Where-Object { $_ -match 'SyntaxError' } | Select-Object -First 1)
                Write-Host ("  [失败] {0} 第 {1} 个内联脚本块（长度 {2}）: {3}" -f $name, $idx, $body.Length, $line) -ForegroundColor Red
            } else {
                Write-Host ("  [通过] {0} 第 {1} 个内联脚本块（长度 {2}）" -f $name, $idx, $body.Length) -ForegroundColor Green
            }
            $pos = $e + 9
        }
    }
} finally {
    Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($fail -eq 0) {
    Write-Host ("前端语法检查通过：{0} 个内联脚本块全部 OK" -f $total) -ForegroundColor Green
    exit 0
} else {
    Write-Host ("前端语法检查失败：{0} / {1} 个脚本块有语法错误，看板会整体失效，必须先修复！" -f $fail, $total) -ForegroundColor Red
    exit 1
}
