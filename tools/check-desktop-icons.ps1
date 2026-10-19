param(
    [string]$Root = 'D:\USER\Desktop\BroadcastSystemNet'
)

# 桌面端 Font Awesome 码点校验（需要 node）。
# 背景：WPF 只能吃 .otf/.ttf，码点一旦不在字体里就会变成方块或错位图标；
#       Font Awesome 6 -> 7 有过替换（例如 plus F067 已不再是加号），
#       dotnet build 不会报错，只有肉眼看界面才发现。因此改完 Desktop\*.cs 的图标后跑一次本脚本。

$ErrorActionPreference = 'Stop'

$base = Join-Path $Root 'BroadcastSystemNet'
$meta = Join-Path $base 'res\lib\webfonts\fa7-icons.json'
$desktop = Join-Path $base 'Desktop'

if (-not (Test-Path $meta)) { Write-Host ("缺少图标元数据: " + $meta) -ForegroundColor Red; exit 1 }
if (-not (Test-Path $desktop)) { Write-Host ("缺少目录: " + $desktop) -ForegroundColor Red; exit 1 }

$js = @'
const fs = require('fs');
const path = require('path');
const base = process.argv[2];
const icons = JSON.parse(fs.readFileSync(path.join(base, 'res', 'lib', 'webfonts', 'fa7-icons.json'), 'utf8'));
const byCp = {};
for (const [name, v] of Object.entries(icons)) {
  if (!v.unicode) continue;
  const cp = parseInt(v.unicode, 16);
  if (!(cp in byCp)) byCp[cp] = { name: name, styles: (v.styles || []).join('/') };
}
const used = {};
const dir = path.join(base, 'Desktop');
for (const f of fs.readdirSync(dir).filter(x => x.endsWith('.cs'))) {
  const t = fs.readFileSync(path.join(dir, f), 'utf8');
  for (const m of t.matchAll(/\\u([0-9a-fA-F]{4})/g)) {
    const cp = parseInt(m[1], 16);
    if (cp >= 0xE000 && cp <= 0xF8FF) (used[cp] = used[cp] || []).push(f);
  }
}
const cps = Object.keys(used).map(Number).sort((a, b) => a - b);
let bad = 0;
for (const cp of cps) {
  const e = byCp[cp];
  const hex = 'U+' + cp.toString(16).toUpperCase().padStart(4, '0');
  if (!e) { bad++; console.log('  [FAIL] ' + hex + ' 不在 FA7 图标集中 <- ' + used[cp].join(',')); }
  else console.log('  [OK]   ' + hex + ' ' + e.name + ' (' + e.styles + ')');
}
console.log('');
console.log('ICON_CHECK total=' + cps.length + ' bad=' + bad);
process.exit(bad === 0 ? 0 : 1);
'@

$tmp = Join-Path $env:TEMP ('iconcheck_' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.js')
[System.IO.File]::WriteAllText($tmp, $js, (New-Object System.Text.UTF8Encoding($false)))

try {
    $out = & node $tmp $base 2>&1 | Out-String
    Write-Host $out.TrimEnd()
    if ($LASTEXITCODE -ne 0) {
        Write-Host "桌面端图标码点校验失败：存在 FA7 图标集之外的码点，界面会显示成方块，必须先修复！" -ForegroundColor Red
        exit 1
    }
    Write-Host "桌面端图标码点校验通过。" -ForegroundColor Green
    exit 0
} finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}
