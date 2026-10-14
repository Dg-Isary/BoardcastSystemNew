package com.broadcast.app

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.broadcast.app.ui.AppState
import com.broadcast.app.ui.BroadcastAppShell
import com.broadcast.app.ui.BroadcastTheme

/**
 * 主界面（Compose + MIUIX）：底部 Dock 五个页签（总览/播控/排程/调休/设置）。
 *
 * 稳定性约定：**建界面阶段的任何异常都不许变成"一启动就闪退"** ——
 * 出错时改成把异常直接显示在屏幕上（用户能截图发出来），并写 logcat。
 */
class MainActivity : ComponentActivity() {

    // ================= 息屏/唤醒自愈（用户反馈：息屏久了会卡死、无法继续组网，必须退出重进）=================
    // 根因：以前既没有 WakeLock/WifiLock，也没有亮屏处理 —— 息屏后 CPU/Wi-Fi 被系统挂起、UDP socket 失效，
    //       醒来后没有任何"重新组网"的动作，只能杀掉进程重开。这里补两件事：
    //   ① 组网期间持有 PARTIAL_WAKE_LOCK + WIFI_MODE_FULL_HIGH_PERF：让 CPU/Wi-Fi 在息屏时也不休眠；
    //   ② 监听亮屏/解锁广播：醒来先做一次健康检查，必要时**原地重连**（不再需要退出重进）。
    private var wakeLock: android.os.PowerManager.WakeLock? = null
    private var wifiLock: android.net.wifi.WifiManager.WifiLock? = null
    private var wakeReceiver: android.content.BroadcastReceiver? = null

    private fun acquireLocks() {
        try {
            if (wakeLock == null) {
                val pm = getSystemService(android.content.Context.POWER_SERVICE) as android.os.PowerManager
                wakeLock = pm.newWakeLock(android.os.PowerManager.PARTIAL_WAKE_LOCK, "cbs:vnt").also {
                    it.setReferenceCounted(false); it.acquire()
                }
            }
            if (wifiLock == null) {
                val wm = applicationContext.getSystemService(android.content.Context.WIFI_SERVICE) as android.net.wifi.WifiManager
                @Suppress("DEPRECATION")
                wifiLock = wm.createWifiLock(android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF, "cbs:wifi").also {
                    it.setReferenceCounted(false); it.acquire()
                }
            }
        } catch (t: Throwable) { }
    }

    private fun releaseLocks() {
        try { wakeLock?.let { if (it.isHeld) it.release() }; wakeLock = null } catch (t: Throwable) { }
        try { wifiLock?.let { if (it.isHeld) it.release() }; wifiLock = null } catch (t: Throwable) { }
    }

    private fun registerWakeReceiver() {
        try {
            val r = object : android.content.BroadcastReceiver() {
                override fun onReceive(ctx: android.content.Context?, intent: android.content.Intent?) {
                    try {
                        val s = state
                        if (!s.configured) return
                        val act = intent?.action ?: ""
                        s.log("息屏唤醒（$act）：检查组网状态（已入网=${s.registered}）")
                        // 醒来先刷新一次；如果已经掉线，就**原地重连**（以前这里什么都不做 → 必须退出重进）
                        if (s.registered) s.refreshSys() else s.restartNow()
                    } catch (t: Throwable) { }
                }
            }
            val f = android.content.IntentFilter().apply {
                addAction(android.content.Intent.ACTION_SCREEN_ON)
                addAction(android.content.Intent.ACTION_USER_PRESENT)
            }
            registerReceiver(r, f)
            wakeReceiver = r
        } catch (t: Throwable) { }
    }

    private lateinit var state: AppState

    /** 系统声音（内录）授权框：结果交给 AppState（它会建立采集或回落到"关"）。 */
    private val projectionLauncher =
        registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.StartActivityForResult()) { res ->
            state.onProjectionResult(res.resultCode, res.data)
        }

    /** 从相册选二维码图片：系统相册选择器（不需要存储权限），拿到后用 ZXing 解码。 */
    private var galleryCallback: ((String?) -> Unit)? = null
    private val galleryLauncher =
        registerForActivityResult(androidx.activity.result.contract.ActivityResultContracts.PickVisualMedia()) { uri ->
            val cb = galleryCallback
            galleryCallback = null
            if (uri == null) { cb?.invoke(null) } else {
                com.broadcast.app.ui.decodeQrFromUriVerbose(this, uri, { m -> state.log(m) }) { text -> cb?.invoke(text) }
            }
        }

    /**
     * 物理音量键调"送话音量"。
     *
     * 只在**正在送话**时接管（此时音量键 = 调送给电脑的声音大小，数字增益），并吃掉按键避免同时把
     * 手机媒体音量也改了；没在送话时按键照常走系统（不影响你平时的音量习惯）。
     */
    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        if (event.action == android.view.KeyEvent.ACTION_DOWN && state.micLive) {
            when (event.keyCode) {
                android.view.KeyEvent.KEYCODE_VOLUME_UP -> {
                    state.log("物理音量键：送话音量 " + state.adjustSendGain(10) + "%")
                    return true
                }
                android.view.KeyEvent.KEYCODE_VOLUME_DOWN -> {
                    state.log("物理音量键：送话音量 " + state.adjustSendGain(-10) + "%")
                    return true
                }
            }
        }
        return super.dispatchKeyEvent(event)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        state = AppState(applicationContext)
        // 界面上点「系统声音」开关时，由这里弹系统框（必须先有前台服务，见 MediaProjectionService）
        state.projectionRequester = {
            try {
                val mpm = getSystemService(android.media.projection.MediaProjectionManager::class.java)
                projectionLauncher.launch(mpm.createScreenCaptureIntent())
            } catch (t: Throwable) {
                state.log("拉起系统采集授权失败：" + (t.message ?: t.javaClass.simpleName))
                state.onProjectionResult(RESULT_CANCELED, null)
            }
        }
        // 相册扫码（图标按钮）→ 系统相册选择器
        state.galleryQrPicker = { cb ->
            galleryCallback = cb
            try {
                galleryLauncher.launch(
                    androidx.activity.result.PickVisualMediaRequest(
                        androidx.activity.result.contract.ActivityResultContracts.PickVisualMedia.ImageOnly
                    )
                )
            } catch (t: Throwable) {
                galleryCallback = null
                state.log("打开相册失败：" + (t.message ?: t.javaClass.simpleName))
                cb(null)
            }
        }
        // 全面屏：显式开启 edge-to-edge（内容铺到状态栏/手势条之下），
        // 再由界面用 WindowInsets 留出安全区（见 ui/App.kt）。
        // 目标 SDK 36（Android 15+）本来就会强制 edge-to-edge，这里显式声明是为了
        // 低版本与全面屏机型行为一致，同时让系统栏图标随主题适配明暗。
        runCatching { enableEdgeToEdge() }
        try {
            setContent {
                val s = remember { state }
                // 皮肤固定为 MIUIX（底栏固定液态玻璃，其他界面固定 MIUIX，不再提供切换）
                BroadcastTheme(themeColor = s.themeColor) {
                    // 界面缩放（设置里可调）：用 LocalDensity 覆盖，**布局尺寸与字号一起缩放**，全局生效
                    val d = androidx.compose.ui.platform.LocalDensity.current
                    androidx.compose.runtime.CompositionLocalProvider(
                        androidx.compose.ui.platform.LocalDensity provides androidx.compose.ui.unit.Density(
                            d.density * s.uiScale,
                            d.fontScale * s.uiScale
                        )
                    ) {
                        BroadcastAppShell(s)
                    }
                }
            }
        } catch (t: Throwable) {
            showFatal(t)
        }
        requestAudioPermission()
        acquireLocks()          // 息屏期间不让 CPU/Wi-Fi 休眠（否则 socket 失效 → 卡死、无法继续组网）
        registerWakeReceiver()  // 亮屏/解锁时自愈：掉线就地重连，不用退出重进
        // 权限之前就授过（系统不再回调）时也要把采集源按开关状态打开
        runCatching {
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
                state.onAudioPermissionGranted()
        }
        state.log("就绪。")
        // 有上次的扫码配置就直接自动组网（免扫码）；没有才提示去扫码。
        // 组网失败**不会**弹扫码框：会一直自动重连，只有点「重新配置」才提示扫码（用户要求）。
        state.autoConnect()
    }

    /** 建界面失败时的兜底界面：把原因显示出来，而不是白屏消失。 */
    private fun showFatal(t: Throwable) {
        Log.e("BroadcastApp", "界面初始化失败", t)
        runCatching {
            setContentView(
                androidx.compose.ui.platform.ComposeView(this).apply {
                    setContent {
                        Column(
                            Modifier
                                .fillMaxSize()
                                .verticalScroll(rememberScrollState())
                                .padding(16.dp)
                        ) {
                            Text("界面初始化失败（已拦截，未闪退）", color = Color.Red, fontSize = 15.sp)
                            Text(t.toString(), color = Color.Red, fontSize = 12.sp)
                            Text(t.stackTraceToString().take(3000), fontSize = 10.sp)
                        }
                    }
                }
            )
        }.onFailure {
            // 连 Compose 都起不来时，退回最朴素的 TextView（保证有东西可看）
            setContentView(TextView(this).apply {
                text = "界面初始化失败：\n$t"
                setTextColor(android.graphics.Color.RED)
                textSize = 12f
            })
        }
    }

    private fun requestAudioPermission() {
        runCatching {
            val audioGranted = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
            val cameraGranted = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
            if (!audioGranted || !cameraGranted) {
                requestPermissions(
                    arrayOf(Manifest.permission.RECORD_AUDIO, Manifest.permission.CAMERA), 1
                )
            }
        }
    }

    override fun onDestroy() {
        try { wakeReceiver?.let { unregisterReceiver(it) } } catch (t: Throwable) { }
        wakeReceiver = null
        releaseLocks()
        super.onDestroy()
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 1) {
            val audioOk = grantResults.isNotEmpty() && grantResults[0] == PackageManager.PERMISSION_GRANTED
            if (audioOk) {
                state.log("麦克风权限已授予，现在可以开始喊话了。")
                state.onAudioPermissionGranted()
            } else {
                state.log("麦克风权限被拒：开麦会失败。请到「设置 → 应用 → 广播系统手控端 → 权限」里手动允许录音权限。")
            }
        }
    }
}
