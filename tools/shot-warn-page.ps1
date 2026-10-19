# 预警播报页面截图验证（临时工具）：启动程序 → 等自动拉取 → 点左侧导航 → 截图
param(
  [string]$Exe = "D:\USER\Desktop\BroadcastSystemNet\.buildZG\bin\Debug\net8.0-windows\BroadcastSystemNet.exe",
  [string]$Out = "D:\USER\Desktop\BroadcastSystemNet\shot-warn-page.png",
  [int]$WaitSec = 42,
  [int]$NavY = 297
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Shot2 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int extra);
'@

Get-Process -Name BroadcastSystemNet -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill(); $_.WaitForExit(4000) | Out-Null } catch {} }
Start-Sleep -Seconds 2

Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Seconds $WaitSec

# 点左侧导航「预警播报」
[Shot2.Native]::SetCursorPos(85, $NavY) | Out-Null
Start-Sleep -Milliseconds 300
[Shot2.Native]::mouse_event(0x0002, 0, 0, 0, 0)
Start-Sleep -Milliseconds 60
[Shot2.Native]::mouse_event(0x0004, 0, 0, 0, 0)
Start-Sleep -Milliseconds 1500

[Shot2.Native]::SetCursorPos(1000, 700) | Out-Null   # 鼠标移开，避免悬浮高亮
Start-Sleep -Milliseconds 400

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "已截图: $Out"
