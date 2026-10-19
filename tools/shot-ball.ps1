# 悬浮球自检气泡截图验证脚本（临时工具）：点球 → 截图 → 移开鼠标 → 再截图
param(
  [string]$Exe = "D:\USER\Desktop\BroadcastSystemNet\.buildZG\bin\Debug\net8.0-windows\BroadcastSystemNet.exe",
  [string]$Out1 = "D:\USER\Desktop\BroadcastSystemNet\shot-popup.png",
  [string]$Out2 = "D:\USER\Desktop\BroadcastSystemNet\shot-after-leave.png"
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace Shot -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int extra);
'@

Get-Process -Name BroadcastSystemNet -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit(4000) | Out-Null } catch {} }
Start-Sleep -Seconds 2

Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Seconds 14

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$size = 64
$ballX = $wa.Right - $size - 24
$ballY = $wa.Bottom - $size - 24
$x = $ballX + 32
$y = $ballY + 32
Write-Output "球心: $x,$y  (工作区 $($wa.Width)x$($wa.Height))"

[Shot.Native]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 400
[Shot.Native]::mouse_event(0x0002, 0, 0, 0, 0)
Start-Sleep -Milliseconds 60
[Shot.Native]::mouse_event(0x0004, 0, 0, 0, 0)
Start-Sleep -Milliseconds 700

# 把鼠标移进气泡（模拟用户去看自检结果），此时气泡必须保持不收起
$px = $ballX - 12 - 210
$py = $ballY + 60
if ($py -gt $wa.Bottom - 30) { $py = $wa.Bottom - 30 }
if ($px -lt 10) { $px = 10 }
[Shot.Native]::SetCursorPos($px, $py) | Out-Null
Start-Sleep -Milliseconds 1200
Write-Output "鼠标已移到气泡内: $px,$py"

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
$bmp.Save($Out1, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "已截图: $Out1"

# 鼠标移到左上角 → 气泡应自动收起
[Shot.Native]::SetCursorPos(5, 5) | Out-Null
Start-Sleep -Milliseconds 1600
$bmp2 = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp2.Size)
$bmp2.Save($Out2, [System.Drawing.Imaging.ImageFormat]::Png)
$g2.Dispose(); $bmp2.Dispose()
Write-Output "已截图: $Out2"
