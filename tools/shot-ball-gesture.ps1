# 验证悬浮球手势（临时工具）：双击球 → 截图，应看到主界面；单击球 → 截图，应看到自检气泡
param(
  [string]$Exe = "D:\USER\Desktop\BroadcastSystemNet\.buildZS\bin\Debug\net8.0-windows\BroadcastSystemNet.exe",
  [string]$OutDbl = "D:\USER\Desktop\BroadcastSystemNet\shot-ball-dblclick.png",
  [string]$OutSingle = "D:\USER\Desktop\BroadcastSystemNet\shot-ball-single.png",
  [switch]$SkipDouble
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace BallShot -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int extra);
'@

function Click($x, $y, $count) {
  [BallShot.Native]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 300
  for ($i = 0; $i -lt $count; $i++) {
    [BallShot.Native]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 40
    [BallShot.Native]::mouse_event(0x0004, 0, 0, 0, 0)
    if ($i -lt $count - 1) { Start-Sleep -Milliseconds 90 }   # 两次点击间隔 < 系统双击时间
  }
}
function Shoot($path) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output ("已截图: " + $path)
}

Get-Process -Name BroadcastSystemNet -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit(4000) | Out-Null } catch {} }
Start-Sleep -Seconds 2

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
# 球的位置按配置里记的 ball_x/ball_y（用户拖过就不在默认位置了），读不到才退回右下角默认值
$ballX = $wa.Right - 64 - 24
$ballY = $wa.Bottom - 64 - 24
$cfg = Join-Path $env:LOCALAPPDATA 'CampusBroadcastSystem\config\setting.jsonl'
if (Test-Path $cfg) {
  $g = (Get-Content $cfg -Encoding UTF8 | Select-Object -First 1) | ConvertFrom-Json
  if ($null -ne $g.ball_x) { $ballX = [double]$g.ball_x }
  if ($null -ne $g.ball_y) { $ballY = [double]$g.ball_y }
}
$cx = [int]($ballX + 32)
$cy = [int]($ballY + 32)
Write-Output ("球位置（配置）：$ballX,$ballY → 点击点 $cx,$cy")

Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Seconds 14

if (-not $SkipDouble) {
  Click $cx $cy 2
  Start-Sleep -Milliseconds 1500
  [BallShot.Native]::SetCursorPos(600, 500) | Out-Null   # 移开鼠标，避免悬浮高亮
  Start-Sleep -Milliseconds 400
  Shoot $OutDbl
}

# 再验证单击：主窗口开着也没关系（气泡是 Topmost，会盖在上面）
Click $cx $cy 1
Start-Sleep -Milliseconds 1100      # 等过"系统双击间隔"，单击动作才会触发
[BallShot.Native]::SetCursorPos(600, 500) | Out-Null   # 鼠标移开但还在气泡 2 秒容错内
Start-Sleep -Milliseconds 300
Shoot $OutSingle
