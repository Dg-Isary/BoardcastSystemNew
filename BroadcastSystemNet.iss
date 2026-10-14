; 智能校园广播系统 - Inno Setup 安装包脚本
; 说明：本脚本使用相对路径定位发布产物，可直接在工作目录下编译，不再依赖任何机器专属路径。
; 编译前请先执行发布命令（在工作目录下）：
;   dotnet publish BroadcastSystemNet\BroadcastSystemNet.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o BroadcastSystemNet\bin\Release\net8.0-windows\publish

[Setup]
; 基础信息
; 版本号：由 tools\build-release.ps1 从 BroadcastSystemNet.csproj 的 <Version> 读出后用 /DAppVersion= 传入，
; 做到"只改 csproj 一处"；如果手工直接编译本脚本（没传参数），就用下面的默认值。
#ifndef AppVersion
  #define AppVersion "4.0.0"
#endif
AppName=智能校园广播系统
AppVersion={#AppVersion}
AppPublisher=Designed by Isary
AppVerName=智能校园广播系统 4.0
AppId={{C177B88C-0D3B-422E-A3CC-BF00AEB105EC}
; 明确告诉 Inno Setup：我知道我在用当前用户的 AppData，不要再弹警告了
UsedUserAreasWarning=no

; 安装到系统的 Program Files 目录，需要管理员权限
DefaultDirName={autopf}\智能校园广播系统

; 开始菜单文件夹名称
DefaultGroupName=智能校园广播系统

; 强制申请管理员权限安装
PrivilegesRequired=admin

; 最终生成的安装包文件名和存放位置
OutputDir=.\Output
OutputBaseFilename=BroadcastSystem_Setup_v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
; 允许用户在安装时选择是否创建桌面快捷方式
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 0. 编译前防空校验：发布产物不存在或缺少音频/配置时，给出明确指引，而不是抛出难以理解的 "No files found matching"
#define PublishRoot "BroadcastSystemNet\bin\Release\net8.0-windows\publish"
#if !DirExists(PublishRoot)
  #error 找不到发布目录 BroadcastSystemNet\bin\Release\net8.0-windows\publish。请先在工作目录执行 tools\build-release.ps1（或先运行 dotnet publish）。
#endif
#if FindFirst(PublishRoot + "\res\ring\*", 0) == 0
  #error 发布目录里的 res\ring 是空的。请先执行 tools\build-release.ps1 重新发布（该脚本会处理发布时资源被误删的问题）。
#endif

; 1. 核心程序：打包 publish 目录，但【精准排除】 res\ring 和 res\config 文件夹（它们释放到用户 AppData，避免占用双倍空间）
Source: "BroadcastSystemNet\bin\Release\net8.0-windows\publish\*"; DestDir: "{app}"; Excludes: "res\ring,res\ring\*,res\config,res\config\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; 2. 动态数据：初始铃声与配置释放到用户 AppData（若已存在则不覆盖，保护用户数据）
Source: "BroadcastSystemNet\bin\Release\net8.0-windows\publish\res\ring\*"; DestDir: "{localappdata}\CampusBroadcastSystem\ring"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist
Source: "BroadcastSystemNet\bin\Release\net8.0-windows\publish\res\config\*"; DestDir: "{localappdata}\CampusBroadcastSystem\config"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist

[Icons]
; 开始菜单快捷方式
Name: "{group}\智能校园广播系统"; Filename: "{app}\BroadcastSystemNet.exe"
Name: "{group}\卸载 智能校园广播系统"; Filename: "{uninstallexe}"

; 公共桌面快捷方式
Name: "{commondesktop}\智能校园广播系统"; Filename: "{app}\BroadcastSystemNet.exe"; Tasks: desktopicon

; 开机自启（启动文件夹快捷方式）
Name: "{autostartup}\智能校园广播系统"; Filename: "{app}\BroadcastSystemNet.exe"

[InstallDelete]
; 老版本（或按用户/按机器两种安装方式混用）可能在另一个启动文件夹里也留下一份快捷方式，
; 安装时先清掉，避免系统里出现两条同名启动项；程序内的「系统设置 → 开机自启」也会统一合并成一条。
Type: files; Name: "{userstartup}\智能校园广播系统.lnk"
Type: files; Name: "{commonstartup}\智能校园广播系统.lnk"

[Run]
; 安装完成后询问是否立即运行
Filename: "{app}\BroadcastSystemNet.exe"; Description: "{cm:LaunchProgram,智能校园广播系统}"; Flags: nowait postinstall skipifsilent
