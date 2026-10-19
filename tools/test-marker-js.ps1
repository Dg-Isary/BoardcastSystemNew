$ErrorActionPreference = 'Stop'
$root = 'D:\USER\Desktop\BroadcastSystemNet'
$html = [System.IO.File]::ReadAllText("$root\BroadcastSystemNet\res\index.html", [System.Text.Encoding]::UTF8)

function Get-JsFunction([string]$src, [string]$name) {
    $idx = $src.IndexOf("function $name(")
    if ($idx -lt 0) { throw "找不到函数 $name" }
    $depth = 0; $started = $false; $i = $idx
    while ($i -lt $src.Length) {
        $ch = $src[$i]
        if ($ch -eq '{') { $depth++; $started = $true }
        elseif ($ch -eq '}') { $depth--; if ($started -and $depth -eq 0) { return $src.Substring($idx, $i - $idx + 1) } }
        $i++
    }
    throw "函数 $name 括号不配平"
}

$names = @('parseMarker','holidayMarkerCycle','stepValue','setMarkerValue','getTiaoxiuMarker','getTaskMarker','wheelHolidayMarker','wheelTaskMarker','resetTiaoxiuMarker','resetTaskMarker')
$funcs = @()
foreach ($n in $names) { $funcs += (Get-JsFunction $html $n) }
$funcText = $funcs -join "`n"

$test = @"
// ---- 从 index.html 抽取的真实滚轮逻辑 ----
let currentTiaoxiuRecords = [{marker: 1}, {marker: 3}];
let currentTasks = [{tiaoxiu_marker: 2}];
const store = {};
function fakeEl(id, value) { store[id] = { id: id, value: String(value), title: '' }; return store[id]; }
const document = { getElementById: function (id) { return store[id] || null; } };
fakeEl('txMarker', 0);
fakeEl('tiaoxiu_marker', 1);
$funcText

function line(t) { console.log(t); }
function expect(cond, msg) { if (!cond) { line('  [FAIL] ' + msg); globalThis.bad = true; } else { line('  [PASS] ' + msg); } }
globalThis.bad = false;

line('【调休标记】筛选模式，已有标记 = 0,1,3');
line('  可选序列 = ' + JSON.stringify(holidayMarkerCycle()));
expect(JSON.stringify(holidayMarkerCycle()) === JSON.stringify([0,1,3]), '只在已有标记之间循环（0,1,3）');
const h1 = store.txMarker; h1.value = '0';
wheelHolidayMarker({ preventDefault: function(){} , deltaY: -1 }, h1);
expect(h1.value === '1', '向上滚 0 -> 1');
wheelHolidayMarker({ preventDefault: function(){}, deltaY: -1 }, h1);
expect(h1.value === '3', '向上滚 1 -> 3（跳过未出现的 2）');
wheelHolidayMarker({ preventDefault: function(){}, deltaY: -1 }, h1);
expect(h1.value === '0', '向上滚 3 -> 0（回到开头）');
wheelHolidayMarker({ preventDefault: function(){}, deltaY: 1 }, h1);
expect(h1.value === '3', '向下滚 0 -> 3（循环到末尾）');
h1.value = '99';
expect(getTiaoxiuMarker() === 0, '非法值 99 被纠正为 0');

line('【任务标记】0-10 自由循环');
const t1 = store.tiaoxiu_marker; t1.value = '0';
wheelTaskMarker({ preventDefault: function(){}, deltaY: -1 }, t1);
expect(t1.value === '1', '向上滚 0 -> 1');
for (let i = 0; i < 9; i++) wheelTaskMarker({ preventDefault: function(){}, deltaY: -1 }, t1);
expect(t1.value === '10', '连续向上滚到 10');
wheelTaskMarker({ preventDefault: function(){}, deltaY: -1 }, t1);
expect(t1.value === '0', '10 再向上滚回到 0（循环）');
wheelTaskMarker({ preventDefault: function(){}, deltaY: 1 }, t1);
expect(t1.value === '10', '向下滚 0 -> 10（反向循环）');
t1.value = '-3';
expect(getTaskMarker() === 1, '非法值 -3 被纠正为 1');
t1.value = '42';
expect(getTaskMarker() === 1, '越界值 42 被纠正为 1');

resetTiaoxiuMarker(); resetTaskMarker();
expect(store.txMarker.value === '0' && store.tiaoxiu_marker.value === '1', '复位：调休=0，任务=1');

line(globalThis.bad ? 'HAS-FAIL' : 'ALL-PASS');
"@

$outFile = "$root\.marker-test.js"
[System.IO.File]::WriteAllText($outFile, $test, (New-Object System.Text.UTF8Encoding($false)))
& node $outFile
Write-Output ("node 退出码: " + $LASTEXITCODE)
Remove-Item $outFile -Force
