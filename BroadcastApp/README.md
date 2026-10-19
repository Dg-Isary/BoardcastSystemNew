# BroadcastApp — 广播系统手控端（安卓）

手机端 App：**Jetpack Compose + MIUIX** 界面，协议层与电脑端 C# 实现**逐字节对齐**（同字段号、同布局）。  
本目录同时包含「纯 JVM 协议层 + 验证工程」和「正式的 Compose 安卓工程」。

```
BroadcastApp\
  protocol\src\main\kotlin\...\vntlite\   vnt-lite 协议层（纯 JVM，电脑端/手机端共用一份源码）
  android\                               正式的安卓工程（Gradle + Compose + MIUIX）
    app\src\main\java\com\broadcast\app\  UI / 音频采集 / 扫码
    app\src\main\res\font\fa_solid_900.otf  Font Awesome Solid（界面图标一律用它，禁用 Emoji）
  verify\src\main\kotlin\...\verify\      协议验证程序（离线单元 + 联网实测）
  build-android.bat                       出 APK（Gradle，唯一支持的方式）
  build-verify.bat                        跑协议验证（kotlinc + java，不需要 Gradle/联网）
```

## 一、怎么出 APK

```
BroadcastApp\build-android.bat            :: release（产物 dist\BroadcastApp.apk）
BroadcastApp\build-android.bat debug      :: debug（产物 dist\BroadcastApp-debug.apk）
```

- 用项目自带的 Gradle（`BroadcastApp\tools\gradle-9.5.0`）+ Android Studio 自带 JDK(JBR)；
- 依赖缓存在 `<仓库根>\.gradle-home`（首次构建需要网络；`android\gradle.properties` 里已配代理）；
- **必须用 Gradle 出包**：界面是 Compose + MIUIX，老的 `build-apk.bat`（kotlinc → d8 → aapt2）  
  解析不了 `androidx.compose.*` / `top.yukonga.miuix.*`，也 dex 不了那些 AAR 依赖 ——  
  它只会产出**没有界面**的旧包。所以 `build-apk.bat` 现在只是一层转发，直接调 `build-android.bat`。
- 产物约 **9.5 MB**（Compose 运行时的正常体积；旧包 1.79 MB 就是"没打进去界面"的证据）。

## 二、安装与首次配置（全部扫码，不用手输）

1. 传 APK 到手机安装（未知来源需允许）；
2. 首次打开会申请**相机**（扫码）与**录音**（喊话）权限，都允许；
3. 电脑端点「话筒(\_M)」→「vnt 设置」→ 填服务器/令牌/数据密码 → 点「显示二维码」；
4. 手机端「设置」→「扫一扫配置」→ 系统相机拍那个二维码 → 服务器、令牌、数据密码、  
   对端虚拟 IP、各端口一次性填好；
5. 回到「总览」→「启动组网」→ 状态行显示**已入网**并给出手机自己的虚拟 IP；
6. 电脑端「话筒开关」窗打开，用手机「设置 → 话筒配对」里的 6 位动态码配对；  
   配对成功后本机会自动进入**信任设备**列表，下次可「信任设备免码连接」；
7. 「开麦（喊话）」→ 手机采集（Opus → Ogg 页 → 每包 AEAD，用配对得到的 `sid`）→ 电脑喇叭出声。

## 三、界面结构

- 顶栏 + 内容区：**MIUIX**（`top.yukonga.miuix.kmp`，Apache-2.0）；
- 底部导航：**液态玻璃 dock**（自绘：`graphicsLayer` + `RenderEffect` 模糊 + 高光描边 + 滑动药丸），  
  5 页：运行总览 / 播控 / 排程 / 调休 / 设置；
- **不提供皮肤切换**（用户明确要求：底栏固定液态玻璃、其它界面固定 MIUIX）；
- 图标一律 **Font Awesome Solid**（`res/font/fa_solid_900.otf` + `ui/FaIcons.kt`），  
  源码里禁止任何 Emoji（门禁：`tools\check-no-emoji.ps1`，已覆盖本目录的 `.kt/.xml/.md`）；
- 适配全面屏：`contentWindowInsets = WindowInsets(0,0,0,0)` + 手势条区域留白，  
  dock 自己处理底部 inset，不做主题切换。

## 四、vnt 服务端加密（-W）：自动适配

电脑端与手机端**同一套语义**：

- 第一次握手**永远明文**；服务端（vnts）只要 `rsa_cipher` 可用，握手响应里就会带 RSA 公钥；
- 收到公钥就**自动**补发 `SecretHandshakeRequest`（RSA 加密）并切到加密通道，之后**只有控制面**  
  （Service/Control）加密；数据面（IpTurn/OtherTurn）**必须保持明文**，否则 vnts 不转发；
- 服务端没带公钥就一直明文 —— 于是「服务器带 -W」和「不带 -W」**都能连**，用户不用管开关；
- 例外：传输模式选「中继优先」时会自动关掉 -W（vnts 只原样转发明文数据包）；
- 电脑端有兜底：发了密钥握手 4 秒没等到加密响应，就降级成明文继续注册，避免永远卡在"握手中"。

## 五、怎么验证协议（不需要 Gradle、不需要联网）

```
BroadcastApp\build-verify.bat                    :: 离线单元验证
BroadcastApp\build-verify.bat 127.0.0.1:29873    :: 再跑联网验证（需先起 vnts）
```

联网前先起本地服务端（不要带 `-f`，输出要重定向，否则它不退出）：

```
third_party\vnts-runtime\vnts.exe -p 29873 -P 0 > vnts.log 2>&1
```

> vnts 运行件已移到 `third_party\vnts-runtime\`（**不入版本库**，只作本地测试用；不参与打包）。

- `protocol\` 是纯 JVM 代码（只用 `java.*` / `javax.crypto`），所以能脱离 Android 直接编译运行；
- 验证覆盖：报文头、protobuf 编解码、AEAD 布局、`-W` 密钥握手、UDP 配对一问一答、  
  中继路径双向 AEAD 等。

## 六、已修掉的真机问题（留档，避免重犯）

1. **一启动就闪退**：`Color.parseColor("#0B5")` —— Android 不接受 3 位颜色缩写，抛  
   `IllegalArgumentException`，且该行不在 `safe{}` 覆盖范围。已改成 6 位 `#00BB55`，  
   建界面代码整体搬进 `buildUi()`，由 `onCreate` 用 `try/catch(Throwable)` 兜住。
2. **配对成功但没声音**：`MicSender` 原先拿 `System.currentTimeMillis()` 当 `sid`，  
   电脑端按 sid 查不到会话就全丢。现改为**必须用配对返回的 `sid`**，否则「开麦」直接拦住并提示先配对。
3. **Android 13 以下没有 X25519 时闪退**：`MicPairing.isSupported()` 原先只 catch `Exception`，  
   而缺 `java.security.spec.NamedParameterSpec` 抛的是 `NoClassDefFoundError`（属于 `Error`）→ 已改为 catch `Throwable`。
4. **开麦报"地址无效"**：话筒服务原先绑定虚拟 IP，而本程序**不建虚拟网卡**，那个地址在本机不存在。  
   已改为绑定 `0.0.0.0` + 按 CIDR 网段过滤来源（`_vnetPrefixFilter`）。

## 七、抓崩溃栈（下次遇到直接把这段结果贴出来）

```
adb logcat -c
adb shell am start -n com.broadcast.app/.MainActivity
adb logcat -d | findstr /C:"FATAL" /C:"AndroidRuntime" /C:"Caused by"
```

MuMu 模拟器：adb 在 `C:\Program Files\Netease\MuMu\nx_main\adb.exe`，  
需 `set ANDROID_ADB_SERVER_PORT=5037` 并 `adb connect 127.0.0.1:16384`  
（不指定端口会和 Android SDK 的 adb 抢 5037，表现为 `device offline`）。

## 八、与电脑端的对接要点

- 契约文档：`docs\话筒功能接口文档.md`（§2 二维码载荷、§3 配对、§4 音频包、§14 vnt-lite 传输）；
- 电脑端实现：`BroadcastSystemNet\VntLite\*.cs`、`BroadcastSystemNet\MicProtocol.cs`、`MicPairing.cs`；
- **红线**：不要把某个人的私有服务器地址 / 令牌 / 公网 IP 写进代码、文档或安装包默认值。
