package com.broadcast.app.ui

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.broadcast.app.core.ApiClient
import com.broadcast.app.core.ScannedConfig
import com.broadcast.app.core.ScanConfigParser
import com.broadcast.vntlite.VntLiteClient
import com.broadcast.vntlite.VntPacket
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** 一条界面日志（排错用；所有网络/异常都往这里写）。 */
data class LogLine(val time: String, val text: String)

/**
 * 应用状态（刻意做成普通类而不是 ViewModel：这个 App 只有一块界面，少一个依赖）。
 *
 * 连接相关的字段全部来自**扫一扫**（用户要求：不要手输）：
 *   · vnt 组网：服务器 / 令牌 / 数据密码 / protocol / model / finger
 *   · 话筒：对端虚拟 IP / 音频端口 / 配对端口
 *   · 网页：http 端口（cbs-api 走它）
 */
class AppState(private val appContext: android.content.Context? = null) {
    // ---- 外观 ----
    // 皮肤固定为 MIUIX（底栏固定液态玻璃，其他界面固定 MIUIX，不再提供切换）
    /** 主题色（色块 key）：**写进配置，重启后保持**（用户反馈重启被重置）。 */
    var themeColor by mutableStateOf("blue")
        private set

    fun applyThemeColor(key: String) {
        val k = key.ifBlank { "blue" }
        themeColor = k
        prefs?.edit()?.putString("theme_color", k)?.apply()
        log("主题色已切换：$k（已保存，重启后保持）")
    }

    /** 当前页签（0=总览 … 4=设置）。放在状态里，总览页的"点状态跳设置"才能切页。 */
    var tabIndex by mutableStateOf(0)

    /** 顶栏「＋」菜单是否展开（扫码 / 话筒配对都收在这里，其它地方不再出现）。 */
    var showQuickMenu by mutableStateOf(false)

    /** 是否弹出"话筒配对"对话框。 */
    var showPairDialog by mutableStateOf(false)

    /**
     * 上次连接的二维码原文（落盘保存）。
     *
     * 用户要求："app 保存上次连接记录，如果能连接就直接完成组网，无需扫码，若连接失败才提示扫码"。
     * 做法：把扫到的**二维码原文**存起来，启动时重新解析一次 —— 不额外做字段映射，
     * 电脑端以后往二维码里加字段也能自动继承。
     */
    private val prefs by lazy { appContext?.getSharedPreferences("cbs-app", android.content.Context.MODE_PRIVATE) }
    init {
        // 【4.0.0 · nonce 根治】把数据通道（sid=0）的 64 位序号计数器落到 SharedPreferences。
        // 数据通道的密钥是由数据密码派生的静态密钥（所有手机共用一把），所以 seq 必须
        // 跨重启不重叠：这里持久化、只增不减，首次运行取 62 位随机起点。
        // 不注入也能跑（退化为内存计数器 + 随机起点），但那样重启后就有重叠风险。
        try {
            com.broadcast.vntlite.MicProtocol.DataSeqStore.init(
                prefs?.getLong("data_seq", 0L)?.takeIf { it > 0 },
                { v -> try { prefs?.edit()?.putLong("data_seq", v)?.apply() } catch (_: Throwable) { } }
            )
        } catch (_: Throwable) { }
    }
    private var savedQrText: String = ""

    /** 已入网（拿到虚拟 IP）。注意与 [connected]（组网客户端在跑）区分。 */
    var registered by mutableStateOf(false)

    /** 正在用上次配置自动组网（界面提示用）。 */
    var autoConnecting by mutableStateOf(false)

    /** 自动重连次数（界面显示"重试中…"用）。 */
    var reconnectCount by mutableStateOf(0)

    /**
     * 本机在 vnt 里的**设备 ID：必须持久化**（第一次启动生成，以后一直用它）。
     *
     * 以前每次 `startNetwork` 都 `UUID.randomUUID()` → 服务端按设备 ID 分配虚拟 IP，
     * 于是"每次启动组网 IP 都变"；自动重连每重试一次还会再变（用户反馈"IP 频繁变动"）。
     * 而且电脑端「信任设备」记的就是这个 ID —— 一变就等于换了台手机，永远信任不上。
     */
    val phoneDeviceId: String by lazy {
        val saved = prefs?.getString("device_id", null)
        if (!saved.isNullOrBlank()) saved
        else UUID.randomUUID().toString().also { id ->
            try { prefs?.edit()?.putString("device_id", id)?.apply() } catch (_: Throwable) { }
        }
    }

    // ---- 连接配置（来自二维码） ----
    var vntServer by mutableStateOf("")
    var vntToken by mutableStateOf("")
    var dataPassword by mutableStateOf("")
    var protocol by mutableStateOf("udp")
    var model by mutableStateOf("aes_gcm")
    var useFinger by mutableStateOf(false)
    var relayFirst by mutableStateOf(true)
    var peerVirtualIp by mutableStateOf("")
    /** 电脑端在虚拟网里的地址（二维码 `mic.vnt_ip`）：手机经 vnt-lite 中继发数据就用它。 */
    var pcVntIp by mutableStateOf("")
    /** 电脑端的局域网地址（二维码 `mic.lan_ip`）：手机唯一能用裸 UDP 直发的地址。 */
    var pcLanIp by mutableStateOf("")
    /** 实测能通的电脑端虚拟 IP（配对成功后记住，音频/接口都用它）。 */
    var activePcVip by mutableStateOf(0)
    var audioPort by mutableStateOf(29880)
    var ctlPort by mutableStateOf(29881)
    var httpPort by mutableStateOf(5000)
    var hostName by mutableStateOf("")

    // ---- 运行状态 ----
    var connected by mutableStateOf(false)
    var pairing by mutableStateOf(false)
    var paired by mutableStateOf(false)
    var pairingSid by mutableStateOf(0)
    var myVirtualIp by mutableStateOf("")
    var statusLine by mutableStateOf("请先扫码完成配置")
    var lastError by mutableStateOf("")

    // ---- 喊话（话筒）----
    var micOn by mutableStateOf(false)
    var micStatus by mutableStateOf("未开麦")
    var micFrames by mutableStateOf(0)

    /** 正在开麦（后台线程初始化麦克风/编码器，界面据此禁用重复点击）。 */
    var startingMic by mutableStateOf(false)

    // ---- 扫码 ----
    var showScan by mutableStateOf(false)

    // ---- 数据（来自 cbs-api）----
    var dataStatus by mutableStateOf("未连接")
    var sysJson by mutableStateOf<JSONObject?>(null)
    var tasksJson by mutableStateOf<JSONArray?>(null)
    var holidayJson by mutableStateOf<JSONArray?>(null)
    var musicJson by mutableStateOf<JSONArray?>(null)
    var lastDataAt by mutableStateOf(0L)

    /** 界面日志（最多保留 300 行）。 */
    val logs = mutableStateListOf<LogLine>()

    // ---- 运行时对象（不参与 Compose 快照，只做 volatile 引用）----
    @Volatile var vnt: VntLiteClient? = null
    @Volatile var api: ApiClient? = null
    @Volatile var mic: com.broadcast.app.MicSender? = null
    @Volatile private var pollThread: Thread? = null
    @Volatile private var running = false
    /** 正在等配对回包的队列（收包线程往里投 cbs-pair 帧）。 */
    @Volatile private var pairWaiter: java.util.concurrent.ArrayBlockingQueue<ByteArray>? = null

    fun log(text: String) {
        val t = java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.US).format(java.util.Date())
        logs.add(LogLine(t, text))
        while (logs.size > 300) logs.removeAt(0)
    }

    /** 是否已经扫码配置过。 */
    val configured: Boolean
        get() = vntServer.isNotBlank() && vntToken.isNotBlank()

    // ================= 上次连接的保存 / 读取（免扫码直连） =================

    private fun rememberQr(text: String) {
        savedQrText = text
        try { prefs?.edit()?.putString("qr", text)?.apply() } catch (_: Throwable) { }
    }

    /** 有没有存过上次的配置。 */
    fun hasSavedConfig(): Boolean =
        savedQrText.isNotBlank() || !(prefs?.getString("qr", null).isNullOrBlank())

    /** 忘掉上次配置（"重新扫码"时用；只影响自动直连，不影响当前会话）。 */
    fun forgetSavedConfig() {
        savedQrText = ""
        try { prefs?.edit()?.remove("qr")?.apply() } catch (_: Throwable) { }
    }

    /**
     * 启动时调用：有上次配置就**直接自动组网**（不弹扫码）；没有才提示去扫码。
     * 组网失败不会弹扫码框，只会一直自动重连（用户要求：只有点「重新配置」才提示扫码）。
     */
    fun autoConnect() {
        if (running) return
        val saved = savedQrText.ifBlank { prefs?.getString("qr", null) ?: "" }
        if (saved.isBlank()) {
            log("还没有配置过：点「扫一扫配置」扫电脑端「vnt 设置」窗里的二维码（只需一次）")
            statusLine = "请先扫码完成配置"
            return
        }
        val cfg = ScanConfigParser.parse(saved) { log("上次保存的配置无法解析：$it（请重新扫码）") } ?: return
        loadDevices()
        loadSendGain()          // 恢复上次的送话音量（物理音量键调的也是它）
        loadCredentialForCurrentDevice()   // 恢复**当前这台电脑**的设备凭证（按电脑分别保存）
        uiScale = (prefs?.getFloat("ui_scale", 1.0f) ?: 1.0f).coerceIn(0.85f, 1.30f)
        themeColor = prefs?.getString("theme_color", "blue") ?: "blue"   // 恢复主题色（重启保持）
        applyScanResult(cfg)
        log("已载入上次连接配置（${cfg.deviceName.ifBlank { "广播主机" }} @ ${cfg.server}），自动组网中…")
        autoConnecting = true
        statusLine = "正在用上次的配置自动组网…"
        startNetwork { }
    }

    /** 把扫码解析出的配置写进状态。 */
    fun applyScanResult(cfg: ScannedConfig, qrText: String? = null) {
        vntServer = cfg.server
        vntToken = cfg.token
        dataPassword = cfg.wPassword
        protocol = cfg.protocol
        model = cfg.model
        useFinger = cfg.finger
        peerVirtualIp = cfg.lanIp.ifBlank { cfg.micHost }    // 只可能是局域网地址（电脑端保证不发虚拟地址）
        pcVntIp = cfg.vntIp
        pcLanIp = cfg.lanIp
        activePcVip = 0                    // 重新扫码后重新探测哪条地址能通
        audioPort = cfg.audioPort
        ctlPort = cfg.ctlPort
        httpPort = cfg.httpPort
        hostName = cfg.deviceName
        if (!qrText.isNullOrBlank()) { rememberQr(qrText); log("配置已保存：下次启动自动组网，不用再扫码") }
        log("扫码成功：主机「${cfg.deviceName.ifBlank { "未命名" }}」服务器 ${cfg.server}，对端 ${cfg.micHost}:${cfg.ctlPort}")
        if (cfg.vntIp.isNotBlank()) log("电脑端虚拟地址：${cfg.vntIp}（配对/喊话都经 vnt 中继发给它）")
        if (cfg.dataPasswordBlank) log("提醒：二维码里没有数据密码 —— 电脑端「vnt 设置」填一个，否则 UDP 配对通道不可用")
        // 记进"广播系统"设备列表（用户要求：以主机名区分，同名覆盖、异名新增）
        if (!qrText.isNullOrBlank()) upsertDevice(cfg, qrText)
    }

    // ================= 广播系统设备列表（多台主机管理） =================

    /**
     * 一台广播系统。**以主机名区分**（用户要求）：同名 = 同一台，扫到就覆盖它的连接配置；
     * 异名 = 新增一条。[alias] 是用户起的外显名称（默认取主机名）。
     */
    data class DeviceEntry(
        val key: String,          // 主机名（空则用 服务器#令牌 兜底）
        val hostName: String,
        val alias: String,
        val qr: String,           // 这台主机的完整二维码原文（切换时直接用它重新组网）
        val lastUsedAt: Long
    )

    val devices = mutableStateListOf<DeviceEntry>()
    /** 当前正在使用（或最近使用）的那台。 */
    var currentDeviceKey by mutableStateOf("")

    /** 从二维码文本里粗略取出"服务器:端口"，用来判断两台广播系统是不是同一台机器。 */
    private fun serverOfQr(qr: String): String =
        Regex("""\d{1,3}(?:\.\d{1,3}){3}:\d+""").find(qr)?.value ?: qr.take(48)

    /**
     * 生成"不会互相覆盖"的设备 key（用户要求：**重名要自动分配序列 id**）。
     * 规则：
     *   · 主机名没被占用 → 直接用主机名（保持原行为）；
     *   · 主机名已被占用，且**服务器也相同** → 视为同一台机器 → 仍然用主机名（重扫同机 = 覆盖更新  ）；
     *   · 主机名已被占用但**服务器不同** → 是另一台机器（导入配置时最容易遇到）→ 依次分配 `主机名#2`、`#3`…
     * 这样既不会把两台同名机器合并成一条，也不会因为重扫同一台机器而多出一条。
     */
    private fun uniqueDeviceKey(cfg: ScannedConfig, qrText: String): String {
        val base = deviceKeyOf(cfg)
        val srv = serverOfQr(qrText)
        val clash = devices.firstOrNull { it.key == base }
        if (clash == null) return base
        if (serverOfQr(clash.qr) == srv) return base          // 同一台机器：保持覆盖更新
        for (n in 2..99) {
            val k = "$base#$n"
            if (devices.none { it.key == k }) return k
        }
        return "$base#" + (System.currentTimeMillis() % 100000)
    }
    private fun deviceKeyOf(cfg: ScannedConfig): String =
        cfg.deviceName.trim().ifBlank { cfg.server.trim() + "#" + cfg.token.trim() }

    /**
     * 取"当前这台电脑"的设备凭证。**必须按电脑分别保存**：
     * 凭证 = HMAC(该电脑的 cred_secret, 设备ID)，两台电脑的密钥不同，混用会导致另一台校验失败
     * （真机表现：同一台手机连第二台电脑时，第二台加不进管理员）。
     */
    private fun loadCredentialForCurrentDevice() {
        val p = prefs
        val key = currentDeviceKey
        val per = if (p != null && key.isNotBlank()) p.getString("device_cred_$key", "") ?: "" else ""
        // 兼容旧版：没分机存过就退回全局那一份
        deviceCredential = per.ifBlank { p?.getString("device_cred", "") ?: "" }
        api?.let { it.credential = deviceCredential }
    }
    private fun loadDevices() {
        try {
            val raw = prefs?.getString("devices", null) ?: return
            val arr = org.json.JSONArray(raw)
            devices.clear()
            for (i in 0 until arr.length()) {
                val o = arr.optJSONObject(i) ?: continue
                val key = o.optString("key"); val qr = o.optString("qr")
                if (key.isBlank() || qr.isBlank()) continue
                devices.add(
                    DeviceEntry(
                        key, o.optString("host"), o.optString("alias").ifBlank { o.optString("host") },
                        qr, o.optLong("at", 0L)
                    )
                )
            }
            currentDeviceKey = prefs?.getString("current_device", "") ?: ""
        loadCredentialForCurrentDevice()
            if (devices.isNotEmpty()) log("已载入 ${devices.size} 台广播系统的连接配置")
        } catch (t: Throwable) { log("设备列表读取失败（已忽略）：" + (t.message ?: "")) }
    }

    private fun saveDevices() {
        try {
            val arr = org.json.JSONArray()
            devices.forEach { d ->
                arr.put(org.json.JSONObject().apply {
                    put("key", d.key); put("host", d.hostName); put("alias", d.alias)
                    put("qr", d.qr); put("at", d.lastUsedAt)
                })
            }
            prefs?.edit()?.putString("devices", arr.toString())?.putString("current_device", currentDeviceKey)?.apply()
        } catch (_: Throwable) { }
    }

    /** 扫码/手动配置后登记：同名覆盖，异名新增（用户要求）。 */
    fun upsertDevice(cfg: ScannedConfig, qrText: String) {
        val key = uniqueDeviceKey(cfg, qrText)   // 重名（不同机器）自动分配序列 id
        val display = cfg.deviceName.trim().ifBlank { "未命名主机" }
        val idx = devices.indexOfFirst { it.key == key }
        // 重名（不同机器同名）时 key 形如 主机名#2，别名也标上序号，列表里才分得清
        val shownName = if (key.contains("#")) "$display (${key.substringAfterLast('#')})" else display
        val entry = DeviceEntry(key, display, if (idx >= 0) devices[idx].alias.ifBlank { shownName } else shownName, qrText, System.currentTimeMillis())
        if (idx >= 0) { devices[idx] = entry; log("设备列表：已更新「${entry.alias}」（同名覆盖）") }
        else { devices.add(entry); log("设备列表：新增「${entry.alias}」（共 ${devices.size} 台）") }
        currentDeviceKey = key
        loadCredentialForCurrentDevice()   // 换电脑 = 换凭证
        saveDevices()
    }

    /** 改外显名称（用户要求：可设置外显名称）。 */
    fun renameDevice(key: String, alias: String) {
        val idx = devices.indexOfFirst { it.key == key }
        if (idx < 0) return
        val a = alias.trim().ifBlank { devices[idx].hostName }
        devices[idx] = devices[idx].copy(alias = a)
        saveDevices()
        log("设备列表：外显名称改为「$a」")
    }

    /**
     * 编辑某台广播系统的**连接配置**（用户要求：设备行的编辑按钮取代下面的"手动配置"）。
     * 做法：按新参数重新生成一条二维码原文写回该设备记录 —— 与扫码得到的配置**完全同构**，
     * 所以切换设备/自动组网都走同一套逻辑，不会出现"手动填的跟扫码的不是一回事"。
     */
    fun updateDeviceConfig(key: String, alias: String, server: String, token: String, pw: String,
                           vip: String, lan: String, audioPort: Int, ctlPort: Int, httpPort: Int) {
        val idx = devices.indexOfFirst { it.key == key }
        if (idx < 0) return
        val old = devices[idx]
        if (server.isBlank() || token.isBlank()) { log("保存失败：服务器与组网令牌必填"); return }
        val cfg = ScannedConfig(
            server = server.trim(), token = token.trim(), deviceId = "",
            protocol = protocol, model = model, finger = useFinger, wPassword = pw,
            micHost = lan.trim(), audioPort = audioPort, ctlPort = ctlPort, httpPort = httpPort,
            deviceName = old.hostName, vntIp = vip.trim(), lanIp = lan.trim()
        )
        val qr = JSONObject().apply {
            put("t", "cbs-mic"); put("v", 1)
            put("vnt", JSONObject().apply {
                put("server", cfg.server); put("token", cfg.token); put("protocol", cfg.protocol)
                put("device_id", ""); put("model", cfg.model); put("finger", cfg.finger)
                put("w_password", cfg.wPassword)
            })
            put("mic", JSONObject().apply {
                put("host", cfg.micHost); put("port", cfg.audioPort); put("ctl_port", cfg.ctlPort)
                put("http_port", cfg.httpPort); put("name", cfg.deviceName); put("vnt_ip", cfg.vntIp)
                put("lan_ip", cfg.lanIp)
            })
        }.toString()
        devices[idx] = old.copy(alias = alias.trim().ifBlank { old.hostName }, qr = qr, lastUsedAt = System.currentTimeMillis())
        saveDevices()
        log("设备列表：已更新「${devices[idx].alias}」的连接配置（服务器 ${cfg.server}）")
        if (key == currentDeviceKey) {
            // 正在用的这台：立刻按新配置重组网
            applyScanResult(cfg, qr)
            if (running) restartNow()
        }
    }

    /** 删除一台（用户要求：有删除按钮）。 */
    fun removeDevice(key: String) {
        val idx = devices.indexOfFirst { it.key == key }
        if (idx < 0) return
        val gone = devices[idx].alias
        devices.removeAt(idx)
        if (currentDeviceKey == key) currentDeviceKey = ""
        saveDevices()
        log("设备列表：已删除「$gone」（共 ${devices.size} 台）")
    }

    /** 切换到另一台广播系统：重新组网，并且**会话要重新配对**（不同电脑的信任列表不一样）。 */
    fun switchToDevice(key: String) {
        val e = devices.firstOrNull { it.key == key } ?: return
        val cfg = ScanConfigParser.parse(e.qr) { log("「${e.alias}」的配置无法解析：$it") } ?: return
        log("正在切换到「${e.alias}」（主机 ${e.hostName}，服务器 ${cfg.server}）")
        paired = false; pairingSid = 0; api?.let { it.sid = 0; it.sessionKey = ByteArray(0) }
        micOn = false; try { mic?.stop() } catch (_: Throwable) { }; mic = null
        applyScanResult(cfg, e.qr)
        forgetPairingAutoTried()          // 换主机后允许重新自动免码配对
        if (running) restartNow() else startNetwork { }
    }

    /** 换主机后重新给"自动免码配对"一次机会。 */
    private fun forgetPairingAutoTried() { autoPairTried = false }

    // ================= 组网 =================

    /** 启动 vnt-lite 组网（内部会把收到的 TypeApi 包交给 ApiClient）。 */
    fun startNetwork(onChanged: () -> Unit) {
        if (running) { log("组网已在运行"); return }
        if (!configured) { log("还没配置：请先扫一扫"); return }
        // 【必须在后台线程】组网第一步就是 UDP 发包（握手）。
        // 以前这里直接在 UI 线程跑，Android 的 StrictMode 会抛
        // NetworkOnMainThreadException（真机日志：BlockGuardOs.sendto），握手包发不出去。
        running = true                      // 先占位，避免连点两次起两个客户端
        Thread {
            if (!buildAndStart()) {
                running = false
                log("组网启动失败，稍后会自动重试")
                return@Thread
            }
            connected = true
            statusLine = "已启动组网，等待分配虚拟 IP…"
            log("已启动 vnt-lite（${if (relayFirst) "中继优先" else "P2P 优先"}）")
            startPolling(onChanged)
        }.also { it.isDaemon = true; it.start() }
    }

    /**
     * 建立客户端并启动（不含轮询线程）。抽出来是为了自动重连时能"原地重启"。
     * @return 是否启动成功
     */
    private fun buildAndStart(): Boolean {
        return try {
            val c = VntLiteClient()
            c.password = dataPassword
            c.relayPlaintext = relayFirst
            c.useFinger = useFinger
            c.deviceId = phoneDeviceId          // 固定设备 ID：虚拟 IP 才稳定，电脑端「信任设备」才记得住
            c.name = "android-" + android.os.Build.MODEL
            val apiClient = ApiClient { pkt -> c.sendPeerViaIp(pkt, peerVip()) }
            apiClient.deviceId = phoneDeviceId          // 电脑端据此判断"是不是管理员设备"（改排程/调休要）
            apiClient.credential = deviceCredential     // 设备凭证（类 cookie）：自动带上，界面不可见
            apiClient.deviceName = android.os.Build.MODEL   // 机型名：电脑端管理员窗口里认设备用
            // 数据通道密钥 = SHA256(数据密码)：配不配对都能调接口（用户要求：播控/排程/调休连上就能用）
            apiClient.dataKey = com.broadcast.vntlite.MicPairUdp.deriveKey(dataPassword)
            api = apiClient
            vnt = c
            c.onPeerData = { data, from ->
                try {
                    // ① 配对回包（cbs-pair 帧）优先交给正在等回复的配对流程
                    if (!deliverToPairWaiters(data)) {
                        // ② 其次当 cbs-api 接口响应
                        apiClient.onPacket(data)
                    }
                } catch (t: Throwable) {
                    log("收包处理异常（已忽略）：${t.message}")
                }
            }
            val (ok, err) = c.start(vntServer, vntToken, c.deviceId, c.name)
            if (!ok) { log("组网启动失败：$err"); vnt = null; api = null }
            ok
        } catch (t: Throwable) {
            log("组网异常（已拦截）：${t.message}")
            false
        }
    }

    /** 自动重连：把旧客户端停掉再来一遍（配置不变，用户无感）。 */
    private fun restartNetwork() {
        try { vnt?.stop() } catch (_: Throwable) { }
        vnt = null; api = null
        registered = false; myVirtualIp = ""
        if (buildAndStart()) {
            connected = true
            statusLine = "自动重连中（第 $reconnectCount 次）…"
        } else {
            connected = false
            statusLine = "自动重连失败，将继续重试…"
        }
    }

    private fun peerVip(): Int {
        if (activePcVip != 0) return activePcVip
        val list = pcVipCandidates()
        return if (list.isEmpty()) 0 else list[0]
    }

    /**
     * 电脑端可能的虚拟 IP 候选（按可能性排序）。
     *
     * 为什么会有多个：电脑上可能同时跑着两套 vnt 客户端。
     * **只把"和我们在同一个虚拟网里"的地址当候选**：
     *   ① 二维码 `mic.vnt_ip` = 电脑端**程序内置 vnt-lite** 的地址（和我们同一个 token/服务器，一定能通）；
     *   ② 设备表里按名字匹配到的地址（同一个网里，保险兜底）。
     * 二维码里的 `mic.vnt_ip_alt` 是"你自己那份 vnt"的网卡地址 —— 它可能挂在**另一个 vnt 网（不同 token/密码）**上，
     * 发过去纯属浪费（实测用户就质疑过"token 都不一样你试它干嘛"），所以**不再当候选**，只作为界面上的诊断信息显示。
     */
    private fun pcVipCandidates(): List<Int> {
        val out = ArrayList<Int>()
        fun add(s: String) { val v = parseVip(s); if (v != 0 && !out.contains(v)) out.add(v) }
        add(pcVntIp)
        val c = vnt
        if (c != null) {
            val mine = myVirtualIp
            val others = c.devices.filter { it.virtualIp != 0 && VntPacket.intToIp(it.virtualIp) != mine }
            // 第一优先：按主机名匹配。**放宽**成"相等 / 互相包含 / 去掉后缀相同" ——
            // vnt 里注册的名字可能带后缀（-android、域名等），只认完全相等会在改名时失效。
            if (hostName.isNotBlank()) {
                val want = hostName.trim().lowercase()
                fun hit(n: String): Boolean {
                    val s = n.trim().lowercase()
                    if (s.isEmpty()) return false
                    if (s == want) return true
                    if (s.contains(want) || want.contains(s)) return true
                    return s.substringBefore('-').substringBefore('.') == want.substringBefore('-').substringBefore('.')
                }
                for (d in others) if (hit(d.name)) add(VntPacket.intToIp(d.virtualIp))
            }
            // 兜底①：网里除了我们自己**只有一台**设备 → 它就是电脑端（IP 变了也能自动找到）
            if (others.size == 1) add(VntPacket.intToIp(others[0].virtualIp))
            // 兜底②：名字对不上、又有多台设备时，**把所有设备都当候选试一遍**。
            // 电脑端在配对答复里会回自己的设备 ID（pdid），所以"试错"是安全的：
            // 只有真正那台电脑会回正确的 ID，别的设备即使收到也不会认（会话密钥对不上）。
            for (d in others) add(VntPacket.intToIp(d.virtualIp))
            // 地址和二维码里写的不一样时，明确记一条日志（"IP 变了"这种情况一眼能看到）
            val byName = others.firstOrNull { hostName.isNotBlank() && it.name.trim().equals(hostName.trim(), ignoreCase = true) }
            if (byName != null) {
                val ipNow = VntPacket.intToIp(byName.virtualIp)
                val ipQr = pcVntIp
                if (ipNow != ipQr && ipNow != lastPcIpNotice) {
                    lastPcIpNotice = ipNow
                    log("电脑端虚拟 IP 已变化：二维码里是 $ipQr，vnt 设备表里现在是 $ipNow —— 已自动改用后者（按主机名 $hostName 找到）")
                }
            }
        }
        return out
    }

    private var lastPcIpNotice = ""

    private fun parseVip(s: String): Int {
        val t = s.trim()
        if (t.isEmpty() || t == "0.0.0.0") return 0
        if (!t.startsWith("10.") && !t.startsWith("192.168.") && !t.startsWith("172.")) return 0
        return try { VntPacket.ipToInt(t) } catch (_: Exception) { 0 }
    }

    /**
     * 配对一问一答。候选地址**逐个试**，最后再试局域网直发：
     *   ① 经 vnt-lite 中继（手机不建网卡时的正路）：把 cbs-pair 帧封进 vnt 数据包发给电脑的虚拟 IP；
     *      候选可能有多个（内置 vnt-lite 的地址 / 你自己那份 vnt 的地址），每个试一小段时间；
     *   ② 局域网直发（同一个 Wi-Fi 时更快）：发到二维码里的 host:ctl_port。
     * 哪条先回就用哪条；全都失败才报错，并把每条的原因都写出来。
     */
    private fun pairExchange(frame: ByteArray, key: ByteArray, timeoutMs: Int): Triple<String?, String?, Boolean> {
        val c = vnt
        var candidates = pcVipCandidates()
        // 【关键】IP 变了以后，缓存里的旧地址会让配对/数据一直发往废地址（用户反馈"重新扫码也连不上"）。
        // 只要缓存地址不在当前候选里，就丢掉它并重解一次。
        if (activePcVip != 0 && !candidates.contains(activePcVip)) {
            log("电脑端地址已失效（缓存 ${VntPacket.intToIp(activePcVip)}），正在重新按 vnt 设备表解析…")
            activePcVip = 0
            candidates = pcVipCandidates()
        }
        val errs = ArrayList<String>()
        if (c != null && candidates.isNotEmpty()) {
            // 每个候选地址的等待时间也自适应（打洞初期慢 → 起步值大，之后实测 RTT 变快 → 自动缩短）
            val adaptive = com.broadcast.app.AdaptiveTimeout.current()
            val per = maxOf(1200, maxOf(timeoutMs, adaptive) / candidates.size)
            for (vip in candidates) {
                val q = java.util.concurrent.ArrayBlockingQueue<ByteArray>(4)
                pairWaiter = q
                try {
                    log("→ 经 vnt 中继发给 " + VntPacket.intToIp(vip) + "（等 ${per}ms）")
                    c.sendPeerViaIp(frame, vip)
                    val deadline = System.currentTimeMillis() + per
                    while (System.currentTimeMillis() < deadline) {
                        val reply = q.poll(150, java.util.concurrent.TimeUnit.MILLISECONDS) ?: continue
                        val (line, err) = com.broadcast.vntlite.MicPairUdp.tryUnwrap(reply, key)
                        if (line != null) {
                            activePcVip = vip          // 记住这条能通的地址，音频/接口都走它
                            log("电脑端地址已确定：" + VntPacket.intToIp(vip))
                            return Triple(line, null, true)
                        }
                        // 收到了但解不开 = 数据密码不一致，这条结论很明确，不用再试别的地址
                        return Triple(null, err, true)
                    }
                    errs.add(VntPacket.intToIp(vip) + " 无回应")
                } catch (t: Throwable) {
                    errs.add(VntPacket.intToIp(vip) + " 发送失败：" + (t.message ?: t.javaClass.simpleName))
                } finally {
                    pairWaiter = null
                }
            }
        } else {
            errs.add(if (c == null) "组网未启动" else "二维码里没有电脑端虚拟 IP")
        }
        // ② 局域网直发（同一个 Wi-Fi 时最快）。
        //    只认**局域网地址**：电脑端保证 mic.lan_ip/mic.host 不会是虚拟网地址（10.x），
        //    这里再做一道保险——凡是虚拟网段的地址都不拿去裸发（手机没有到它的路由，发了也是白发）。
        val lanTargets = listOf(pcLanIp, cfgLanFallback())
            .filter { it.isNotBlank() && it != "0.0.0.0" && parseVip(it) == 0 }
            .distinct()
        for (host in lanTargets) {
            log("→ 局域网直发 $host:$ctlPort（等 ${timeoutMs}ms）")
            val r = com.broadcast.vntlite.MicPairUdp.exchange(host, ctlPort, frame, key, timeoutMs)
            if (r.first != null || r.third) return r
            errs.add("局域网直发($host:$ctlPort) 超时")
        }
        if (lanTargets.isEmpty()) errs.add("没有可直发的局域网地址（只走 vnt 中继）")
        return Triple(null, "电脑端没有任何回复：" + errs.joinToString("；"), false)
    }

    /** 局域网直发的备用地址（二维码里没有 lan_ip 时用 mic.host；同样是虚拟地址就丢掉）。 */
    private fun cfgLanFallback(): String = if (pcLanIp.isBlank()) peerVirtualIp else ""

    /** 收包线程把 cbs-pair 帧交给正在等回复的配对流程（返回 true 表示已消化）。 */
    private fun deliverToPairWaiters(data: ByteArray): Boolean {
        if (!com.broadcast.vntlite.MicPairUdp.looksLikePairFrame(data)) return false
        val q = pairWaiter
        if (q != null) { try { q.offer(data) } catch (_: Throwable) { } }
        return true
    }

    /** 轮询线程：更新虚拟 IP，并按 5 秒节奏拉系统状态（Dock 页面靠它保持新鲜）。 */
    private fun startPolling(onChanged: () -> Unit) {
        pollThread = Thread {
            var tick = 0
            var lastRegisteredAt = System.currentTimeMillis()
            var nextRetryAt = 0L
            while (running) {
                try {
                    val c = vnt
                    if (c != null) {
                        val ip = if (c.virtualIp != 0) VntPacket.intToIp(c.virtualIp) else ""
                        if (ip.isNotEmpty()) {
                            if (!registered) {
                                registered = true
                                autoConnecting = false
                                if (reconnectCount > 0) log("自动重连成功（第 $reconnectCount 次）")
                                reconnectCount = 0
                                statusLine = "已入网：虚拟 IP $ip"
                            }
                            lastRegisteredAt = System.currentTimeMillis()
                            nextRetryAt = 0L
                        } else if (registered) {
                            registered = false
                            lastRegisteredAt = System.currentTimeMillis()
                        }
                        if (ip.isNotEmpty() && ip != myVirtualIp) {
                            myVirtualIp = ip
                            log("已入网：虚拟 IP $ip")
                        }
                        if (ip.isEmpty()) {
                            statusLine = c.statusText() + " · 收 ${c.recvPackets}/发 ${c.sentPackets}"
                        } else {
                            statusLine = c.statusText() + " · $ip · 收 ${c.recvPackets}/发 ${c.sentPackets}"
                        }
                        if (c.lastError.isNotBlank()) lastError = c.lastError
                    }
                    // 自动重连（用户要求：一直重试，不要弹扫码让用户重来）：
                    //   15 秒还没入网就重启客户端，之后退避到 30s / 60s 上限，避免疯狂重连。
                    val now = System.currentTimeMillis()
                    if (!registered && running && now - lastRegisteredAt > 15_000 && now >= nextRetryAt && reconnectCount < 1000) {
                        reconnectCount++
                        val waitMs = when {
                            reconnectCount <= 2 -> 15_000L
                            reconnectCount <= 4 -> 30_000L
                            else -> 60_000L
                        }
                        nextRetryAt = now + waitMs
                        lastRegisteredAt = now
                        log("还没入网，自动重连第 $reconnectCount 次（失败原因：${c?.lastError?.ifBlank { "未收到服务端响应" } ?: "未启动"}）")
                        restartNetwork()
                    }
                    // 每 2 秒刷新一次系统状态：播控页的进度条要靠它走字（数据通道不需要配对）
                    if (dataReady && tick % 2 == 0) refreshSys()
                    if (dataReady && tick % 10 == 0) refreshAdminPanel()
                    // 【关键修复】数据通道一就绪就拉一次列表（真实根因：以前 refreshAll 只在"配对成功"后调用，
                    // 于是普通用户（或配对失败时）**什么都看不到** —— 用户反馈"普通用户看不到排程/调休列表"）。
                    // 设计上数据通道本来就不需要配对（见 dataReady 注释），所以这里与配对无关。
                    if (dataReady && !dataLoadedOnce) { dataLoadedOnce = true; log("数据通道就绪：开始拉取排程/调休/音乐（无需配对）"); refreshAll() }
                    if (dataReady && tick % 60 == 0 && dataLoadedOnce) refreshAll()   // 之后每分钟静默刷新一次   // 开机就查一次角色（管理员免码配对与编辑权限都要用）
                    // 开麦期间每 5 秒拉一次"电脑端音频体检"，写进手机日志 ——
                    // "话筒没声音"从此只要一份手机日志就能定位（是否收到包/OggHead/播放器状态）
                    if (micOn && dataReady && tick % 5 == 0) refreshPcAudioHealth()
                    // 入网后自动尝试"信任设备免码配对"（只试一次；成功就一直是免码状态）
                    if (registered && !paired && !pairing) autoPairTrusted()
                    tick++
                    onChanged()
                } catch (t: Throwable) {
                    log("轮询异常（已忽略）：${t.message}")
                }
                try { Thread.sleep(1000) } catch (_: InterruptedException) { break }
            }
        }.also { it.isDaemon = true; it.start() }
    }

    fun stopNetwork() {
        stopMic()
        running = false
        pollThread = null
        try { vnt?.stop() } catch (_: Throwable) {}
        vnt = null; api = null
        connected = false; registered = false; autoConnecting = false; reconnectCount = 0
        paired = false; pairingSid = 0
        dataStatus = "未连接"
        statusLine = "已停止组网"
        log("已停止组网")
    }

    /** 「立即重试」按钮：不重新扫码，马上把组网重启一遍（后台线程，不能在 UI 线程发包）。 */
    fun restartNow() {
        if (!configured) { log("还没配置：请先扫码"); return }
        if (!running) { startNetwork { } ; return }
        Thread {
            reconnectCount++
            log("手动重试：重新连接（第 $reconnectCount 次）")
            restartNetwork()
        }.also { it.isDaemon = true; it.start() }
    }

    // ================= 喊话（话筒） =================

    /** 开麦：麦克风 → Opus → Ogg → AEAD → 经 vnt-lite 发到电脑端虚拟 IP。 */
    /**
     * 开麦。
     *
     * 【务必在工作线程里做】`MicSender.start()` 会**同步**发出第一个包（OpusHead 页），
     * 而它会被贴手条/总览的按钮在 **UI 线程**上调用 → 真机上就是
     * `NetworkOnMainThreadException @ BlockGuardOs.sendto`（日志里抓到过）。
     * 顺带 AudioRecord/MediaCodec 的初始化也不该占着主线程。
     */
    fun startMic() {
        if (micOn) { log("已在喊话中"); return }
        if (startingMic) return
        if (!paired) { showPairDialog = true; log("喊话需要先配对：已打开配对对话框"); return }
        val key = api?.sessionKey
        if (key == null || key.size != 32) { log("会话密钥无效：请重新配对"); return }
        val peerIp = peerVip()
        if (peerIp == 0) { log("还不知道电脑端在虚拟网里的地址：请确认已入网并重新扫码"); return }
        val c = vnt ?: run { log("组网未启动"); return }
        startingMic = true
        micStatus = "正在开麦…"
        Thread {
            try {
                // 顺带数一下真的发出去了多少帧（以前这个计数没接上，界面一直显示 0）
                val sender = com.broadcast.app.MicSender(pairingSid, key) { pkt ->
                    c.sendPeerViaIp(pkt, peerIp)
                    micFrames++
                }
                val (ok, err) = sender.start()
                if (!ok) {
                    micStatus = "开麦失败：$err"
                    lastError = err ?: ""
                    log("开麦失败：$err")
                    return@Thread
                }
                mic = sender
                micOn = true
                // 【息屏/切后台也能送话】Android 12+ 后台采集麦克风必须有 microphone 类型的前台服务，
                // 否则系统会把麦克风静音；它同时让进程不被冻结 —— 只要不退出 App，话筒就能一直用。
                // 注意：必须在这里（用户点"送话"、App 在前台时）启动，Android 不允许从后台启动 microphone 前台服务。
                appContext?.let { com.broadcast.app.MicForegroundService.start(it) }
                micStatus = "送话中（sid=$pairingSid → ${com.broadcast.vntlite.VntPacket.intToIp(peerIp)}:$audioPort）"
                micFrames = 0
                log("开始喊话：麦克风 → Opus → Ogg → AEAD → ${com.broadcast.vntlite.VntPacket.intToIp(peerIp)}:$audioPort")
            } catch (t: Throwable) {
                micStatus = "开麦异常"
                log("开麦异常（已拦截）：" + com.broadcast.vntlite.VntLiteClient.describeThrowable(t))
            } finally {
                startingMic = false
            }
        }.also { it.isDaemon = true; it.start() }
    }

    /** 关麦（= 静音）：停发送但**保留会话**，随时可以再点一下继续送话。 */
    fun stopMic() {
        if (!micOn) return
        try { mic?.stop() } catch (_: Throwable) {}
        mic = null
        micOn = false
        // 关麦：撤掉话筒前台服务（常驻通知一起消失）
        appContext?.let { com.broadcast.app.MicForegroundService.stop(it) }
        micStatus = if (paired) "已静音（会话 sid=$pairingSid 还在，点一下继续送话）" else "已关麦（共发 $micFrames 帧）"
        log(if (paired) "已静音（麦克风采集已停，会话保留）" else "已停止喊话")
    }

    /**
     * 总览页那个「送话 / 静音」复合按钮：
     *   ① 没配对 → 打开配对对话框（这是唯一需要动态码的地方）；
     *   ② 已配对但静音 → 继续送话；
     *   ③ 正在送话 → 静音（不结束会话）。
     */
    fun toggleMic() {
        when {
            !paired -> { showPairDialog = true; log("喊话需要先配对：已打开配对对话框") }
            micOn -> stopMic()
            else -> startMic()
        }
    }

    /** 总览页话筒状态一行（图标颜色文字都用它，避免三处各写一套判断）。 */
    val micStateText: String
        get() = when {
            !configured -> "未配置"
            !registered -> "等待入网"
            !paired -> "未配对（点一下配对）"
            micOn -> "送话中 · 已发 $micFrames 帧"
            else -> "已静音（会话在，点一下继续送话）"
        }

    /** 话筒是否正在送话（总览页按钮/颜色判据）。 */
    val micLive: Boolean get() = micOn && paired

    // ================= 配对（只用于喊话） =================
    // 说明：手机端**不再有"信任设备"**——信任是电脑端的凭据（电脑记住哪台手机免动态码），
    // 手机这边不需要知道、也不需要展示（用户要求）。

    /**
     * 自动尝试配对（用户要求：**进了信任列表下次就不用再输码，直到电脑端踢出信任**）。
     *
     * 电脑端 `MicPairing.Authenticate` 里"信任设备"是**先于动态码**判断的（`IsTrustedDevice(device_id)`），
     * 所以这里故意用一个占位码 `000000` 去握手：
     *   · 本机在信任列表里 → 电脑端直接放行（不消耗动态码、不需要人输）；
     *   · 不在 → 回 `bad_code`，界面才提示"去电脑端看 6 位码配对一次"。
     * 只试一次，避免刷屏/被限速锁定。
     */
    private fun autoPairTrusted() {
        if (paired || pairing || autoPairTried) return
        if (!dataReady) return
        autoPairTried = true
        log("自动配对：本机设备ID ${phoneDeviceId.take(8)}…（管理员设备或信任设备都免码直连）")
        // 先按【管理员设备】试（用户要求：管理员音频包直接跳过配对、无限时）；不是管理员会自动回退到信任设备路径
        pairWithCode("000000", auto = true, adminMode = true)
    }

    /** 是否已经试过"自动免码配对"（只试一次，避免刷屏和被限速锁定）。 */
    private var autoPairTried = false

    /**
     * 用 6 位数字动态码配对。**只影响喊话**：配对成功后拿到 sid + 会话密钥用于音频包。
     * 播控/排程/调休走的是"数据通道"（见 ApiClient：sid=0 + 数据密码派生密钥），不需要配对。
     *
     * @param auto true = 由"信任设备免码"自动发起（占位码），失败时的提示措辞不同。
     */
    fun pairWithCode(code: String, auto: Boolean = false, adminMode: Boolean = false) {
        if (pairing || paired) return
        if (!adminMode && (code.length != 6 || !code.all { it.isDigit() })) { log("动态码必须是 6 位数字"); return }
        val key = com.broadcast.vntlite.MicPairUdp.deriveKey(dataPassword)
        if (key.size != 32) { log("请先配置数据密码（配对通道用它加密）"); return }
        if (peerVip() == 0 && (peerVirtualIp.isBlank() || peerVirtualIp == "0.0.0.0")) {
            log("还不知道电脑端地址：请确认已入网并重新扫码"); return
        }
        pairing = true
        Thread {
            try {
                val kp = com.broadcast.vntlite.MicPairing.newKeyPair()
                val nc = com.broadcast.vntlite.MicPairing.newNonce()
                val hello = if (adminMode)
                    com.broadcast.vntlite.MicPairing.buildAdminHello(vnt?.deviceId ?: "android")   // 管理员免码：不含 6 位码
                else
                    com.broadcast.vntlite.MicPairing.buildHello(code, vnt?.deviceId ?: "android", kp.publicKey, nc)
                val frame = com.broadcast.vntlite.MicPairUdp.build(hello, key)
                var lastErr = ""
                for (attempt in 1..2) {
                    val timeout = if (attempt == 1) 3000 else 4000
                    log("配对请求 #$attempt（先经 vnt 中继，超时再试局域网直发，等 ${timeout}ms）")
                    val (line, err, gotReply) = pairExchange(frame, key, timeout)
                    if (line == null) {
                        lastErr = err ?: "未知错误"
                        if (!gotReply) lastErr += "（核对：电脑端是否已点开麦、数据密码是否两边一致）"
                        continue
                    }
                    val r = com.broadcast.vntlite.MicPairing.parseReply(line)
                    if (r == null) { lastErr = "回复无法解析：$line"; continue }
                    if (!r.ok) {
                        // pending = 电脑端正在弹窗等管理员确认（弹窗模式尚未接入界面时也会先看到它）
                        if (line.contains("\"pending\":true")) {
                            log("电脑端正在等待管理员确认配对…（若一直无响应，请改用 6 位动态码）")
                            lastErr = "等待管理员确认超时"
                            continue
                        }
                        log("配对被拒：" + pairReasonText(r.reason))
                        lastError = pairReasonText(r.reason)
                        // 自动免码配对失败 → 说明本机还没进电脑端信任列表：提示去输一次动态码
                        if (auto && adminMode && r.reason == "not_admin") {
                            log("本机不是管理员设备，改按【信任设备】免码再试一次…")
                            pairing = false
                            pairWithCode("000000", auto = true, adminMode = false)
                            return@Thread
                        }
                        if (auto && r.reason == "bad_code") {
                            log("本机还不在电脑端信任列表里：请在电脑端「话筒」窗点开麦，把显示的 6 位码输一次（之后就一直免码）")
                            showPairDialog = true
                        }
                        pairing = false
                        return@Thread
                    }
                    val sk = if (adminMode || r.admin)
                        com.broadcast.vntlite.MicPairUdp.adminMicKey(dataPassword, r.serverNonce)   // 管理员：两端共享派生（带每会话盐则密钥每会话唯一）、无限时
                    else
                        com.broadcast.vntlite.MicPairing.deriveSessionKey(kp.privateKey, r.serverPublicKey, code, nc, r.serverNonce)
                    api?.let { it.sid = r.sid; it.sessionKey = sk }
                    paired = true
                    pairingSid = r.sid
                    log(if (adminMode || r.admin) "配对成功（管理员 · 免码 · 无限时）：sid=${r.sid}，会话密钥 ${sk.size} 字节"
                        else "配对成功：sid=${r.sid}，会话密钥 ${sk.size} 字节（只用于喊话）")
                    // 设备凭证（类 cookie）：电脑端在答复里自动下发，这里保存下来，之后每个请求都带上它。
                    // 界面不显示、也不提供复制（用户确认的语义）。
                    com.broadcast.vntlite.MicPairing.lastCredential?.let { c ->
                        if (c.isNotBlank()) {
                            deviceCredential = c
                            // **按电脑分别保存**：每台电脑的 cred_secret 不同，一份凭证只能对一台有效
                            //（用户实测：同一台手机连两台电脑时，另一台会因凭证不符而加不进管理员）
                            val k = currentDeviceKey
                            prefs?.edit()?.apply {
                                if (k.isNotBlank()) putString("device_cred_$k", c)
                                putString("device_cred", c)      // 兼容旧版键
                            }?.apply()
                            api?.let { it.credential = c }
                            log("已获取设备凭证（自动，界面不可见）")
                        }
                    }
                    refreshAll()
                    pairing = false
                    return@Thread
                }
                log("配对失败（两个通道都试过了）：$lastErr")
            } catch (t: Throwable) {
                log("配对异常（已拦截）：" + com.broadcast.vntlite.VntLiteClient.describeThrowable(t))
            } finally {
                pairing = false
            }
        }.also { it.isDaemon = true; it.start() }
    }

    // ================= 数据（cbs-api） =================

    /** 电脑端拒绝原因 → 人话（取值与接口文档 §6 一致）。 */
    fun pairReasonText(reason: String?): String = when (reason) {
        "bad_code" -> "动态码不对或已过期（60 秒一换、用一次作废）：看电脑端「话筒」窗里当前的码再输一次"
        "mic_off" -> "电脑端话筒开关是「关」：请在电脑端「话筒」窗点开麦"
        "busy" -> "电脑端已有话筒在跑：让管理员点『断开当前话筒』再试"
        "too_many" -> "这个来源输错太多次被锁定：等几分钟再试"
        "expired" -> "配对已过期：请在电脑端重新开麦取新码"
        "version" -> "协议版本不符：手机端与电脑端版本不一致"
        "denied" -> "管理员在电脑端拒绝了本次配对"
        "bad_request" -> "请求格式不对（版本不匹配？）"
        else -> reason ?: "未知原因"
    }

    /**
     * 数据通道是否可用：**入网 + 知道数据密码**即可，不需要 6 位配对码
     *（用户要求：话筒才需要配对码，播控/排程/调休连上就能用）。
     */
    val dataReady: Boolean
        get() = registered && dataPassword.length >= 8

    /** 分片/重传计数（导出日志用：确认大响应分片与丢包重传确实在工作）。 */
    val apiChunkStats: String
        get() = api?.let {
            "分片 发 ${it.chunksSent}/收 ${it.chunksReceived}　重传 ${it.retries} 次（救回 ${it.retriesRecovered} 次）"
        } ?: "-"

    private fun callApi(method: String, path: String, body: String? = null): ApiClient.Result? {
        val c = api ?: return null
        if (!dataReady) {
            dataStatus = when {
                !registered -> "还没入网"
                else -> "电脑端没填数据密码（二维码里 w_password 为空）"
            }
            return null
        }
        val r = c.call(method, path, body)
        if (!r.ok) {
            dataStatus = "接口失败：" + (r.error ?: "")
            lastError = r.error ?: ""
        }
        return r
    }

    /**
     * 拉电脑端的"音频链路体检"并写进手机日志。
     *
     * 为什么这么做：话筒没声音时，断点在电脑侧（有没有收到包 / 有没有 OpusHead / ffplay 起没起来）。
     * 手机日志是唯一方便导出的东西，所以让手机定期把电脑那句话抄回来 —— 一份手机日志就能定位。
     */
    fun refreshPcAudioHealth() {
        try {
            val r = callApi("GET", "/api/mic/status")
            if (r == null || !r.ok) {
                // 【不要静默失败】以前拉不到就什么都不记，日志里"没有这一行"到底是没开麦还是拉不到，看不出来
                val why = r?.error ?: "没有数据通道"
                if (why != lastHealthErrorLogged) { lastHealthErrorLogged = why; log("电脑端音频体检拉取失败：" + why) }
                return
            }
            val o = JSONObject(r.body)
            val txt = o.optString("audio_text")
            if (txt.isNotBlank()) {
                pcAudioHealth = txt
                // 只在文本变化时记一行，避免 5 秒刷一条把日志冲掉
                if (txt != lastPcAudioHealthLogged) { lastPcAudioHealthLogged = txt; log("电脑端音频：" + txt) }
            }
        } catch (t: Throwable) {
            val why = com.broadcast.vntlite.VntLiteClient.describeThrowable(t)
            if (why != lastHealthErrorLogged) { lastHealthErrorLogged = why; log("电脑端音频体检异常：" + why) }
        }
    }
    private var lastHealthErrorLogged = ""

    /** 最近一次"电脑端音频体检"文本（界面/日志导出都用它）。 */
    var pcAudioHealth by mutableStateOf("")
    private var lastPcAudioHealthLogged = ""

    /** 麦克风权限授予后由 MainActivity 调用：按开关状态把采集源打开。 */
    fun onAudioPermissionGranted() {
        if (micSourceOn && !com.broadcast.app.PcmMixer.micRunning) setMicSource(true)
        com.broadcast.app.PcmMixer.gain = sendGainPercent / 100f     // 恢复上次的送话音量
    }

    /** 从偏好里读回送话音量（构造时调用一次）。 */
    private fun loadSendGain() {
        val v = prefs?.getInt("send_gain", 100) ?: 100
        sendGainPercent = v.coerceIn(20, 200)
        com.broadcast.app.PcmMixer.gain = sendGainPercent / 100f
    }

    /**
     * 麦克风采集开关（用户要求：麦克风与系统声音是**两个独立开关**，可同时开=混音）。
     */
    var micSourceOn by mutableStateOf(true)
        private set

    /** 系统声音（内录）采集开关。 */
    var sysSourceOn by mutableStateOf(false)
        private set

    /** 由 MainActivity 注入：拉起"屏幕录制/内录"系统授权框。 */
    var projectionRequester: (() -> Unit)? = null

    /** 采集源状态一行话（界面/日志用）。 */
    val audioSourceText: String
        get() {
            val m = if (com.broadcast.app.PcmMixer.micRunning) "麦克风 开" else "麦克风 关"
            val sAm = if (com.broadcast.app.PcmMixer.sysRunning) "系统声音 开" else "系统声音 关"
            val err = com.broadcast.app.PcmMixer.lastError
            return "$m · $sAm" + (if (err.isNotBlank()) "（$err）" else "")
        }

    /** 打开/关闭麦克风采集。 */
    fun setMicSource(on: Boolean) {
        micSourceOn = on
        if (on) {
            val ctx = appContext ?: run { micSourceOn = false; log("麦克风采集打开失败：没有应用上下文"); return }
            val (ok, err) = com.broadcast.app.PcmMixer.startMic(ctx)
            if (!ok) { micSourceOn = false; log("麦克风采集打开失败：" + (err ?: "未知原因")); lastError = err ?: "" }
            else log("麦克风采集已打开")
        } else {
            com.broadcast.app.PcmMixer.stopMic()
            log("麦克风采集已关闭")
        }
        micStatus = audioSourceText
    }

    /**
     * 打开/关闭系统声音（内录）。
     *
     * 流程：先拉起前台服务（Android 14+ 硬要求）→ 弹系统授权框 → 用户在 onProjectionResult 里授权后
     * 才真正建立采集。取消授权就自动回落（开关弹回关）。
     */
    fun setSystemSource(on: Boolean) {
        if (!on) {
            sysSourceOn = false
            com.broadcast.app.PcmMixer.stopSystem()
            appContext?.let { com.broadcast.app.MediaProjectionService.stop(it) }
            log("系统声音采集已关闭")
            micStatus = audioSourceText
            return
        }
        val ctx = appContext ?: run { log("系统声音采集不可用：没有应用上下文"); return }
        val req = projectionRequester
        if (req == null) { log("系统声音采集不可用：界面没有接好授权流程"); return }
        com.broadcast.app.MediaProjectionService.start(ctx)     // 必须先有前台服务
        sysPending = true
        log("正在请求系统声音采集授权（会弹一次系统框，允许后即可内录；不录屏）")
        req()
    }

    /** 系统授权框还开着。 */
    var sysPending by mutableStateOf(false)

    /** MainActivity 把系统授权结果交回来。 */
    fun onProjectionResult(resultCode: Int, data: android.content.Intent?) {
        sysPending = false
        val ctx = appContext
        if (resultCode != android.app.Activity.RESULT_OK || data == null || ctx == null) {
            sysSourceOn = false
            ctx?.let { com.broadcast.app.MediaProjectionService.stop(it) }
            log("系统声音采集授权被取消（随时可以再点开关重试）")
            return
        }
        try {
            val mpm = ctx.getSystemService(android.media.projection.MediaProjectionManager::class.java)
            val mp = mpm.getMediaProjection(resultCode, data)
            val (ok, err) = com.broadcast.app.PcmMixer.startSystem(mp)
            sysSourceOn = ok
            if (!ok) {
                com.broadcast.app.MediaProjectionService.stop(ctx)
                log("系统声音采集失败：" + (err ?: "未知原因"))
                lastError = err ?: ""
            } else log("系统声音采集已打开（注意：部分 App 禁止被内录，通话语音/DRM 音频系统一律不给）")
        } catch (t: Throwable) {
            sysSourceOn = false
            com.broadcast.app.MediaProjectionService.stop(ctx)
            log("系统声音采集异常：" + com.broadcast.vntlite.VntLiteClient.describeThrowable(t))
        }
        micStatus = audioSourceText
    }

    /**
     * 送话音量（百分比 20~200）。界面滑条 + 手机**物理音量键**都改它。
     *
     * 为什么不直接用系统媒体音量：系统音量影响的是本机播放，而我们要调的是"送给电脑的声音大小"
     *（数字增益，做在混音之后、Opus 编码之前），这样内录系统声音时也能单独调。
     */
    /**
     * 设备凭证（类 cookie）：配对成功后由电脑端自动下发，之后每个请求都带上它。
     * **界面不显示、不允许复制**；没被赋权时电脑端也不会把它写进配置。
     */
    var deviceCredential by mutableStateOf("")
        private set

    /** 界面缩放（0.85~1.30）：全局生效，写进配置，重启后保持。 */
    var uiScale by mutableStateOf(1.0f)
        private set

    fun applyUiScale(v: Float) {
        uiScale = v.coerceIn(0.85f, 1.30f)
        prefs?.edit()?.putFloat("ui_scale", uiScale)?.apply()
        log("界面缩放已设为 ${(uiScale * 100).toInt()}%")
    }

    var sendGainPercent by mutableStateOf(100)
        private set

    fun applySendGain(p: Int) {
        val v = p.coerceIn(20, 200)
        sendGainPercent = v
        com.broadcast.app.PcmMixer.gain = v / 100f
        prefs?.edit()?.putInt("send_gain", v)?.apply()
    }

    /** 物理音量键调用：按步长增减送话音量（返回新值，方便界面/日志显示）。 */
    fun adjustSendGain(delta: Int): Int {
        applySendGain(sendGainPercent + delta)
        return sendGainPercent
    }

    /** 由 MainActivity 注入：从相册选一张图，解码出二维码文本（回调 null = 没解出来）。 */
    var galleryQrPicker: (((String?) -> Unit) -> Unit)? = null

    /** 系统状态（轻量接口；播控页要显示进度条，所以按秒级刷新）。 */
    fun refreshSys() {        val r = callApi("GET", "/api/sys") ?: return
        if (!r.ok) return
        val o = try { JSONObject(r.body) } catch (e: Exception) { null }
        if (o != null) {
            sysJson = o
            // 乐观值落地：服务端回读到一致状态就清掉 pending（不一致则继续显示 pending，等下一轮轮询）
            val srvOn = o.optBoolean("global_switch", true)
            globalOnPending?.let { if (it == srvOn) globalOnPending = null }
            syncSeekAck()               // 拖动回执：到了就解除"定位中"
            lastDataAt = System.currentTimeMillis()
            dataStatus = "数据正常（" + android.text.format.DateFormat.format("HH:mm:ss", lastDataAt) + "）"
        } else dataStatus = "系统状态不是 JSON"
    }

    // ---- 管理员面板（手机端「设置」顶部；**只有管理员设备能拿到数据** —— 电脑端门禁保证）----
    /** 是否已经用数据通道拉过一次列表（普通用户不配对也要能看列表）。 */
    private var dataLoadedOnce = false
    var isAdminDevice by mutableStateOf(false)
    var micCode by mutableStateOf("")
    var micCodeLeft by mutableStateOf(0)
    var micDevices by mutableStateOf<List<org.json.JSONObject>>(emptyList())

    /** 拉取管理员面板数据：先取配对码（成功才说明自己是管理员），再取话筒设备列表。 */
    fun refreshAdminPanel() {
        Thread {
            try {
                val c = callApi("GET", "/api/mic/code")
                if (c?.ok != true) {
                    isAdminDevice = false
                    // 【诊断】把失败原因写进手机日志（用户导出日志即可看到）
                    log("管理员面板：/api/mic/code 失败 → " + (c?.error ?: "没有返回（通道/超时）") + "（状态 " + (c?.status ?: 0) + "）")
                    return@Thread
                }
                log("管理员面板：/api/mic/code 成功 → " + c.body.take(60))
                isAdminDevice = true
                val o = org.json.JSONObject(c.body)
                micCode = o.optString("code")
                micCodeLeft = o.optInt("seconds_left", 0)
                val d = callApi("GET", "/api/mic/devices")
                if (d?.ok == true) {
                    val arr = org.json.JSONArray(d.body)
                    val list = ArrayList<org.json.JSONObject>()
                    for (i in 0 until arr.length()) arr.optJSONObject(i)?.let { list.add(it) }
                    micDevices = list
                }
            } catch (t: Throwable) { }
        }.also { it.isDaemon = true; it.start() }
    }

    /** 管理员在手机上踢出某台话筒设备（= 断开 + 解除信任）。 */
    fun kickMicDevice(sid: Int, device: String) {
        Thread {
            try {
                val body = org.json.JSONObject().apply { put("sid", sid); put("device", device) }.toString()
                val r = callApi("POST", "/api/mic/kick", body)
                if (r?.ok == true) log("已在手机上踢出设备：$device（已解除信任，下次连接需重新输配对码）")
                else { val m = "踢出失败：" + (r?.error ?: "没有数据通道"); log(m); lastError = m }
                refreshAdminPanel()
            } catch (t: Throwable) { }
        }.also { it.isDaemon = true; it.start() }
    }

    // ---- 播控 ----

    /** 播控进度（秒）：来自 /api/sys 的 manual_player.elapsed / current_duration。 */
    val playerElapsed: Double
        get() = sysJson?.optJSONObject("manual_player")?.optDouble("elapsed", 0.0) ?: 0.0
    val playerDuration: Double
        get() = sysJson?.optJSONObject("manual_player")?.optDouble("current_duration", 0.0) ?: 0.0

    /** 拖动进度条：手机端先"钉住"目标位置（用户要求：进度条停在手放的位置，不要被旧进度拉回去抖动）。 */
    var pinnedSeek by mutableStateOf<Double?>(null)    /** 已看到的电脑端拖动回执号（变化 = 电脑端已按新位置起播）。 */
    var seekAckSeen by mutableStateOf(0L)

    /** 拖动进度条 → 跳到第 [seconds] 秒（电脑端支持 cmd=seek）。 */
    fun seekTo(seconds: Double) {
        pinnedSeek = seconds.coerceAtLeast(0.0)      // 先钉住，界面不再回跳
        pinnedAt = System.currentTimeMillis()
        playerCmd("seek", seconds)
        log("播控：已请求定位到 ${"%.1f".format(seconds)} 秒（等电脑端回执）")
    }

    /** 读电脑端的拖动回执；回执到了就解除钉住并明确提示"已定位"。 */
    private fun syncSeekAck() {
        val mp = sysJson?.optJSONObject("manual_player") ?: return
        val ack = mp.optLong("seek_ack", 0L)
        // 【兜底 1】换曲了：钉住的位置已经无意义，立刻解除（否则进度条会定在原处、切歌也不动）
        val idx = mp.optInt("current_index", -1)
        if (idx != lastPlayerIndex) {
            lastPlayerIndex = idx
            if (pinnedSeek != null) { pinnedSeek = null; log("播控：已换曲，进度条恢复跟随播放进度") }
        }
        // 【兜底 2】钉了超过 12 秒还没等到回执（电脑端没起播/回执丢了）：也要放开，不能一直定着
        val pinAt = pinnedAt
        if (pinnedSeek != null && pinAt > 0 && System.currentTimeMillis() - pinAt > 12000) {
            pinnedSeek = null
            log("播控：等了 12 秒没等到电脑端定位回执，进度条先恢复跟随（电脑端可能没在播放）")
        }
        if (ack != seekAckSeen) {
            seekAckSeen = ack
            if (pinnedSeek != null) {
                val off = mp.optDouble("seek_offset", 0.0)
                pinnedSeek = null
                log("电脑端已完成定位：${"%.1f".format(off)} 秒（进度条恢复跟随）")
            }
        }
    }

    /** 上一次看到的曲目序号（用于"换曲就清钉"）。 */
    private var lastPlayerIndex = -2
    /** 这次钉住的时刻（用于超时兜底）。 */
    private var pinnedAt = 0L

    /**
     * 当前应该显示的播放位置（秒）——**这是"看到的和听到的一致"的关键**。
     *
     * 规则：
     *   ① 拖动刚结束时：从拖动目标**按时间自己往前走**（不依赖电脑端回执，所以绝不会定在原处）；
     *   ② 电脑端报回的真实进度与上面接近（≤2.5 秒）→ 认为已对齐，改用电脑端真实进度；
     *   ③ 换曲/停止/回执变化 → 立即解除。
     * 另外：即使没拖动，两次轮询之间也按时间插值，进度条不再"两秒一跳"。
     */
    fun displayPositionSeconds(isPlaying: Boolean, polledAtMs: Long): Double {
        val pin = pinnedSeek
        if (pin != null) {
            val advanced = pin + (System.currentTimeMillis() - pinnedAt) / 1000.0
            val real = playerElapsed
            // 电脑端真实进度已追上（说明它按新位置播起来了）→ 解除钉住，改用真实值
            if (isPlaying && kotlin.math.abs(real - advanced) <= 2.5) { pinnedSeek = null; return real }
            return advanced
        }
        // 没拖动：在两次轮询之间按时间插值，进度条平滑推进
        val base = playerElapsed
        return if (isPlaying && polledAtMs > 0) base + (System.currentTimeMillis() - polledAtMs) / 1000.0 else base
    }

    /** 设置播控音量（0..100）。 */
    fun setPlayerVolume(v: Int) {
        playerCmd("volume", v.coerceIn(0, 100))
    }

    // ---- 排程 / 调休 编辑（走数据通道，不需要配对） ----

    /**
     * 新增（id=null）或修改（id=n）一条排程任务。
     *
     * 修改时必须先**从电脑端读回这条任务的原始对象**再补字段 —— 这样电脑端那些手机不认识的字段不会丢；
     * 读不到（列表没拿到 / 任务已被别处删掉）就**拒绝保存**，绝不用一个残缺对象去覆盖（可能清掉配置）。
     */
    fun saveTask(id: Int?, fields: Map<String, Any?>) {
        if (!dataReady) { log("还没入网/没数据密码：无法保存排程"); return }
        Thread {
            try {
                val base = JSONObject()
                if (id != null) {
                    val fresh = getJsonArray("/api/tasks")
                    if (fresh == null) {
                        log("排程保存已取消：读不到电脑端当前任务列表（不用本地缓存覆盖，避免丢配置）")
                        return@Thread
                    }
                    var found = false
                    for (i in 0 until fresh.length()) {
                        val t = fresh.optJSONObject(i) ?: continue
                        if (t.optInt("id", -1) == id) {
                            t.keys().forEach { k -> base.put(k, t.get(k)) }
                            found = true
                            break
                        }
                    }
                    if (!found) {
                        log("排程保存已取消：电脑端已经没有 #$id 这条任务了（可能被别处删了），请刷新后重试")
                        return@Thread
                    }
                }
                fields.forEach { (k, v) -> if (v != null) base.put(k, v) }
                val r = if (id == null) callApi("POST", "/api/tasks", base.toString())
                        else callApi("PUT", "/api/tasks/$id", base.toString())
                if (r?.ok == true) { log(if (id == null) "排程：新增成功" else "排程：已保存任务 $id"); refreshAll() }
                else log("排程保存失败：" + (r?.error ?: "没有数据通道"))
            } catch (t: Throwable) { log("排程保存异常：" + com.broadcast.vntlite.VntLiteClient.describeThrowable(t)) }
        }.also { it.isDaemon = true; it.start() }
    }

    /** 删除一条排程任务。 */
    fun deleteTask(id: Int) {
        if (!dataReady) { log("还没入网/没数据密码：无法删除"); return }
        Thread {
            val r = callApi("DELETE", "/api/tasks/$id")
            if (r?.ok == true) { log("排程：已删除任务 $id"); refreshAll() }
            else log("排程删除失败：" + (r?.error ?: "没有数据通道"))
        }.also { it.isDaemon = true; it.start() }
    }

    /**
     * 保存整份调休表（电脑端 `POST /api/tiaoxiu` 的语义就是"整表替换"）。
     * 每条：{"date":"2026-05-01","marker":0,"action":"disable|enable","note":"劳动节"}
     */
    /**
     * 调休的「读-改-写」。**这是防数据清空的关键**。
     *
     * 电脑端 `POST /api/tiaoxiu` 的语义是**整表替换**：以前我直接拿手机内存里的缓存表去改，
     * 只要那份缓存是空的（刚启动 / 同步失败 / 被清过），一次编辑就会把电脑端的调休表**整表清空**
     * —— 用户真机上就被清过一次，绝不能再犯。
     * 现在：写之前先 GET 拉电脑端的**最新整表**，在最新表上改；拉不到就**拒绝写**并告知用户。
     */
    private fun mutateTiaoxiu(change: (MutableList<JSONObject>) -> String?) {
        if (!dataReady) { log("调休保存已取消：还没入网或没数据密码"); return }
        Thread {
            try {
                val fresh = getJsonArray("/api/tiaoxiu")
                if (fresh == null) {
                    val m = "调休保存已取消：读不到电脑端当前的调休表（不用本地缓存覆盖，避免清空数据）"; log(m); lastError = m
                    return@Thread
                }
                val list = ArrayList<JSONObject>()
                for (i in 0 until fresh.length()) fresh.optJSONObject(i)?.let { list.add(JSONObject(it.toString())) }
                val why = change(list)
                if (why != null) { val m = "调休保存已取消：$why"; log(m); lastError = m; return@Thread }
                val arr = JSONArray()
                list.forEach { arr.put(it) }
                val r = callApi("POST", "/api/tiaoxiu", arr.toString())
                if (r?.ok == true) { log("调休：已保存（电脑端共 ${list.size} 条）"); refreshAll() }
                else { val m = "调休保存失败：" + (r?.error ?: "没有数据通道"); log(m); lastError = m }
            } catch (t: Throwable) {
                log("调休保存异常：" + com.broadcast.vntlite.VntLiteClient.describeThrowable(t))
            }
        }.also { it.isDaemon = true; it.start() }
    }

    /** 同步 GET 一个 JSON 数组（只允许在工作线程里调用；失败返回 null）。 */
    private fun getJsonArray(path: String): JSONArray? {
        val r = callApi("GET", path) ?: return null
        if (!r.ok) return null
        return try { JSONArray(r.body) } catch (e: Exception) { null }
    }

    /** 新增（index < 0）或修改（index >= 0）一条调休：在电脑端最新表上改，然后整表提交。 */
    fun upsertTiaoxiu(index: Int, date: String, action: String, note: String, marker: Int, endDate: String = "") {
        if (date.trim().length < 8) { log("调休：日期格式应为 YYYY-MM-DD"); return }
        mutateTiaoxiu { list ->
            val o = JSONObject().apply {
                // 电脑端两种写法都认：单日 date，或区间 start_date/end_date（与"云端订阅"导入的记录同构）
                if (endDate.trim().length >= 8 && endDate.trim() != date.trim()) {
                    put("start_date", date.trim()); put("end_date", endDate.trim())
                } else {
                    put("date", date.trim())
                }
                put("action", action)
                put("note", note.trim())
                put("marker", marker)
                put("source", "manual")          // 手工新增的记录标成 manual，便于和云端订阅区分
            }
            if (index in list.indices) list[index] = o else list.add(o)
            null
        }
    }

    /** 删除一条调休（同样先读最新表，再整表提交）。 */
    fun removeTiaoxiu(index: Int, itemJson: String? = null) {
        mutateTiaoxiu { list ->
            // 优先**按内容匹配**（日期/动作/标记/备注），下标只作兜底：
            // 手机列表是拉取时的快照，电脑端表一变，下标就会错位 → 以前会误判"这条不存在"而拒绝删除。
            val want = try { if (itemJson.isNullOrBlank()) null else JSONObject(itemJson) } catch (e: Exception) { null }
            fun same(a: JSONObject, b: JSONObject): Boolean {
                fun d(o: JSONObject) = (o.optString("date").ifBlank { o.optString("start_date") }) + "~" + o.optString("end_date")
                return d(a) == d(b) && a.optString("action") == b.optString("action") &&
                       a.optInt("marker", 0) == b.optInt("marker", 0) && a.optString("note") == b.optString("note")
            }
            if (want != null) {
                val hit = list.indexOfFirst { same(it, want) }
                if (hit >= 0) { list.removeAt(hit); return@mutateTiaoxiu null }
            }
            if (index in list.indices) { list.removeAt(index); null }
            else "要删的那条在电脑端表里找不到（可能已被改动）：请点「刷新数据」后重试"
        }
    }

    /** 同步云端调休（电脑端 `/api/tiaoxiu/sync`，与电脑端/网页那颗按钮同一接口）。 */
    fun syncTiaoxiu() {
        if (!dataReady) { log("同步云端调休已取消：还没入网或没数据密码"); return }
        Thread {
            val r = callApi("POST", "/api/tiaoxiu/sync", "{}")
            if (r?.ok == true) {
                log("云端调休同步完成：" + r.body.take(200))
                refreshAll()
            } else log("云端调休同步失败：" + (r?.error ?: "没有数据通道"))
        }.also { it.isDaemon = true; it.start() }
    }

    /**
     * 手动配置（二维码扫不出来时的兜底）：字段与二维码一一对应，
     * 存成与"扫码"完全一样的二维码原文，所以之后照样免扫码自动组网。
     */
    fun applyManualConfig(server: String, token: String, pw: String, vip: String, lan: String,
                          audioPort: Int, ctlPort: Int, httpPort: Int) {
        if (server.isBlank() || token.isBlank()) { log("手动配置：服务器与组网令牌必填"); return }
        try {
            val cfg = ScannedConfig(
                server = server.trim(), token = token.trim(), deviceId = "", protocol = protocol, model = model,
                finger = useFinger, wPassword = pw, micHost = lan.trim(), audioPort = audioPort,
                ctlPort = ctlPort, httpPort = httpPort, deviceName = hostName.ifBlank { "广播主机" },
                vntIp = vip.trim(), lanIp = lan.trim()
            )
            val qr = JSONObject().apply {
                put("t", "cbs-mic"); put("v", 1)
                put("vnt", JSONObject().apply {
                    put("server", cfg.server); put("token", cfg.token); put("protocol", cfg.protocol)
                    put("device_id", ""); put("model", cfg.model); put("finger", cfg.finger)
                    put("w_password", cfg.wPassword)
                })
                put("mic", JSONObject().apply {
                    put("host", cfg.micHost); put("port", cfg.audioPort); put("ctl_port", cfg.ctlPort)
                    put("http_port", cfg.httpPort); put("name", cfg.deviceName); put("vnt_ip", cfg.vntIp)
                    put("lan_ip", cfg.lanIp)
                })
            }.toString()
            applyScanResult(cfg, qr)
            log("手动配置已保存：服务器 ${cfg.server}，电脑虚拟IP ${cfg.vntIp.ifBlank { "-" }}")
        } catch (t: Throwable) {
            log("手动配置失败：" + com.broadcast.vntlite.VntLiteClient.describeThrowable(t))
        }
    }


    /** 三页数据一次拉齐（进页面/手动刷新用）。**不需要配对**：入网 + 有数据密码即可。 */
    fun refreshAll() {
        if (!dataReady) {
            dataStatus = if (!registered) "还没入网" else "电脑端没填数据密码（二维码里 w_password 为空）"
            return
        }
        Thread {
            try {
                val sys = callApi("GET", "/api/sys")
                if (sys?.ok == true) {
                    sysJson = try { JSONObject(sys.body) } catch (e: Exception) { null }
                }
                val t = callApi("GET", "/api/tasks")
                if (t?.ok == true) tasksJson = try { JSONArray(t.body) } catch (e: Exception) { null }

                val h = callApi("GET", "/api/tiaoxiu")
                if (h?.ok == true) holidayJson = try { JSONArray(h.body) } catch (e: Exception) { null }

                val m = callApi("GET", "/api/music")
                if (m?.ok == true) musicJson = try { JSONArray(m.body) } catch (e: Exception) { null }

                lastDataAt = System.currentTimeMillis()
                dataStatus = "数据正常（${android.text.format.DateFormat.format("HH:mm:ss", lastDataAt)}）"
                com.broadcast.app.AdaptiveTimeout.onSample(900.0); log("已拉取：排程 ${tasksJson?.length() ?: -1} 条 / 调休 ${holidayJson?.length() ?: -1} 条 / 音乐 ${musicJson?.length() ?: -1} 条")
            } catch (t: Throwable) {
                dataStatus = "拉取异常：" + t.message
            }
        }.also { it.isDaemon = true; it.start() }
    }

    /** 播控指令（走电脑端现成的 /api/player/cmd）。 */
    fun playerCmd(cmd: String, value: Any? = null) {
        val body = JSONObject().apply {
            put("cmd", cmd)
            if (value != null) put("val", value)
        }.toString()
        Thread {
            val r = callApi("POST", "/api/player/cmd", body)
            if (r?.ok == true) log("播控：$cmd 已下发（HTTP ${r.status}）")
            else log("播控：$cmd 失败（${r?.error ?: "未连接"}）")
        }.also { it.isDaemon = true; it.start() }
    }

    /** 音乐库里的曲目（给播控页选曲用）。 */
    fun musicNames(): List<String> {
        val arr = musicJson ?: return emptyList()
        val out = ArrayList<String>()
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: continue
            val name = o.optString("name")
            if (name.isNotBlank()) out.add(name)
        }
        return out
    }

    /**
     * 本地点按后的"乐观值"：点完立刻翻转界面，等 /api/sys 回读到一致状态就清掉。
     * 没有它的话开关要等下一轮轮询才动，用户会觉得"点了没反应"。
     */
    private var globalOnPending by mutableStateOf<Boolean?>(null)

    /**
     * 全局总开关状态（停用后不打铃、所有音频静音）。
     * 优先用本地点按的乐观值，其次用 /api/sys 的 global_switch（电脑端、网页端、手机端是同一个开关）。
     */
    val globalOn: Boolean
        get() = globalOnPending ?: (sysJson?.optBoolean("global_switch", true) ?: true)

    /** 按音乐编号查名称（排程页显示"播什么"用）。找不到返回 null，由调用方回落到 #编号。 */
    fun musicNameById(id: String): String? {
        val arr = musicJson ?: return null
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: continue
            if (o.optString("id") == id) return o.optString("name").ifBlank { null }
        }
        return null
    }

    /**
     * 把一串音乐编号翻成可读的名称串（排程页用）。
     * 例：["1","2"] -> "预备铃、正式铃"；超过 [max] 个只列前几个并注明总数。
     * 编号 1000 是电脑端合成的"气象预警播报（自动生成）"，它也在 /api/music 里，所以能正常翻出来。
     */
    fun musicNamesOf(ids: org.json.JSONArray?, max: Int = 3): String {
        if (ids == null || ids.length() == 0) return "未选曲目"
        val out = ArrayList<String>()
        for (i in 0 until ids.length()) {
            if (out.size >= max) break
            val id = ids.optString(i)
            out.add(musicNameById(id) ?: "#$id")
        }
        val rest = ids.length() - out.size
        return out.joinToString("、") + if (rest > 0) " 等 ${ids.length()} 首" else ""
    }

    /**
     * 停用 / 启用广播系统（POST /api/settings {global_switch}）。
     * 与网页端、电脑端是同一个开关：停用后电脑端不打铃、也不播放任何音频。
     */
    /**
     * 点总开关：非管理员设备只提示、不下发。
     * 电脑端 MicApiBridge 对写接口也有管理员门禁，非管理员设备即使绕过界面也会被拒。
     */
    fun trySetGlobalSwitch(on: Boolean) {
        if (!isAdminDevice) {
            log("只有管理员设备能停用/启用广播系统（请用管理员手机或电脑端操作）")
            return
        }
        globalOnPending = on          // 立刻翻转界面，不等轮询
        setGlobalSwitch(on)
    }

    fun setGlobalSwitch(on: Boolean) {
        val body = JSONObject().apply { put("global_switch", on) }.toString()
        Thread {
            val r = callApi("POST", "/api/settings", body)
            if (r?.ok == true) {
                log(if (on) "已恢复广播系统运行" else "已停用广播系统（全局静音，不打铃）")
                refreshSys()          // 立刻回读真实状态（回读到一致值会清掉乐观值）
            } else {
                globalOnPending = null    // 失败就回退到服务端真实状态
                log("切换广播总开关失败：${r?.error ?: "未连接"}")
            }
        }.also { it.isDaemon = true; it.start() }
    }

    /** 加入播控队列（电脑端接口：POST /api/player/cmd cmd=add）。 */
    fun queueAdd(index: Int) {
        val arr = musicJson ?: return
        val o = arr.optJSONObject(index) ?: return
        val body = JSONObject().apply {
            put("cmd", "add")
            put("val", o)
        }.toString()
        Thread {
            val r = callApi("POST", "/api/player/cmd", body)
            if (r?.ok == true) log("已加入队列：${o.optString("name")}")
            else log("加入队列失败：${r?.error ?: "未连接"}")
        }.also { it.isDaemon = true; it.start() }
    }
}
