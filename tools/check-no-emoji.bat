@echo off
rem ============================================================
rem  emoji / 违规字符守卫（项目规则：禁止 emoji，图标一律用内置 FontAwesome 字形）
rem
rem  用法：双击运行，或在命令行执行  tools\check-no-emoji.bat
rem  退出码：0 = 通过；1 = 发现违规字符（会把文件与行号列出来）
rem
rem  说明：只检查"真 emoji + 勾叉/警示/彩色符号 + 变体选择符"这些
rem        一眼就能看出是 emoji 的字符；中文标点、以及项目里既有的
rem        制表符装饰（▼ ★ ┖ ╂ 等）不在检查范围内。
rem ============================================================
setlocal
set "ROOT=%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pat = '[\u2460-\u24FF\u2713\u2714\u2717\u2718\u2705\u274C\u26A0\u2B50\u2764\u2728\u2757\u2753\u2755\u2139\uFE0F]|[\uD83C-\uD83E][\uDC00-\uDFFF]|[\u2700-\u27BF]|[\u2190-\u21FF]';" ^
  "$roots = @('BroadcastSystemNet','BroadcastApp');" ^
  "$skip = '\\(bin|obj|build|\.gradle|\.javatools|run_alt|publish|node_modules)\\';" ^
  "$bad = 0;" ^
  "foreach ($r in $roots) { $p = Join-Path '%ROOT%' $r; if (-not (Test-Path $p)) { continue };" ^
  "  $files = Get-ChildItem $p -Recurse -Include *.cs,*.kt,*.xaml,*.xml,*.html,*.bat,*.md -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch $skip };" ^
  "  foreach ($f in $files) { $lines = Get-Content $f.FullName -ErrorAction SilentlyContinue; for ($i=0; $i -lt $lines.Count; $i++) {" ^
  "    $m = [regex]::Matches($lines[$i], $pat); if ($m.Count -gt 0) {" ^
  "      $c = ($m | ForEach-Object { '[' + $_.Value + ']U+' + ([int][char]$_.Value).ToString('X4') }) -join ' ';" ^
  "      Write-Host ($f.FullName.Replace('%ROOT%\','') + ':' + ($i+1) + '  ' + $c) -ForegroundColor Yellow; $bad++ } } } };" ^
  "if ($bad -eq 0) { Write-Host 'emoji 守卫：通过（未发现 emoji / 违规符号）' -ForegroundColor Green; exit 0 }" ^
  "else { Write-Host ('emoji 守卫：发现 ' + $bad + ' 行违规，请改成内置 FontAwesome 字形') -ForegroundColor Red; exit 1 }"
endlocal
