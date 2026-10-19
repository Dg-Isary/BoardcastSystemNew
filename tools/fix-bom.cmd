@echo off
rem ============================================================
rem  Fix BOM for PowerShell scripts in tools\
rem
rem  Why: Windows PowerShell 5.1 reads a .ps1 file as GBK/ANSI when
rem  it has no UTF-8 BOM. Any script containing Chinese then fails to
rem  parse with mojibake errors. This file is ASCII-only on purpose so
rem  it can always run, even when every other script is broken.
rem
rem  Usage: double-click it, or run:  tools\fix-bom.cmd
rem ============================================================
setlocal
set "HERE=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dir = '%HERE%'; $n = 0; Get-ChildItem -LiteralPath $dir -Filter *.ps1 -File | ForEach-Object { $b = [System.IO.File]::ReadAllBytes($_.FullName); $hasBom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF); if (-not $hasBom) { $t = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8); [System.IO.File]::WriteAllText($_.FullName, $t, (New-Object System.Text.UTF8Encoding($true))); Write-Host ('  added BOM: ' + $_.Name); $n++ } }; if ($n -eq 0) { Write-Host '  all .ps1 files already have a UTF-8 BOM.' } else { Write-Host ('  fixed ' + $n + ' file(s).') }; Write-Host ''; Write-Host 'Syntax check (PowerShell 5.1 parser):'; Get-ChildItem -LiteralPath $dir -Filter *.ps1 -File | ForEach-Object { $e = $null; $t = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$t, [ref]$e); Write-Host ('  ' + $_.Name.PadRight(30) + ' errors=' + $e.Count) }"
echo.
pause
