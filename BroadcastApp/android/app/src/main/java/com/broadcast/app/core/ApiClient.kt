package com.broadcast.app.core

import com.broadcast.vntlite.MicProtocol
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

/**
 * cbs-api 客户端（手机端）：通过**已配对的加密通道**调用电脑端的 HTTP 接口。
 *
 * 协议（与电脑端 `MicApiBridge` 一致，载荷是 UTF-8 JSON）：
 *   请求：`{"v":1,"id":"<唯一>","m":"GET|POST","p":"/api/...","b":{...可选...}}`
 *   响应：`{"v":1,"id":"<同一个>","s":200,"d":"<接口原始正文>"}` 或 `{"v":1,"id":"...","e":"<错误说明>"}`
 *
 * 为什么能让 Dock 三页有数据：手机到不了电脑的 5000 端口，但**能把加密包发到电脑**；
 * 电脑端收到后转发到本机回环 HTTP 接口，再把响应原样塞回来。业务逻辑只有一份（电脑端的接口）。
 *
 * 发送方式：把 MicProtocol 包交给 [MicSenderFn]（产品里是 vnt-lite 的 `sendPeerViaIp`），
 * 因此**不新增任何通道**，AEAD/防重放/限速/会话白名单全部沿用。
 */
class ApiClient(private val sender: (ByteArray) -> Unit) {

    /** 电脑端返回的错误（含 http 错误码与说明），界面据此给人话提示。 */
    data class Result(val ok: Boolean, val status: Int, val body: String, val error: String?)

    private val waiting = ConcurrentHashMap<String, Pair<CountDownLatch, Array<Result?>>>()
    private val seq = AtomicInteger(0)

    /** 会话号（**喊话**配对成功后由界面设置；0 = 走数据通道）。 */
    var sid: Int = 0

    /** 会话密钥（喊话配对成功后由界面设置）。 */
    var sessionKey: ByteArray = ByteArray(0)

    /**
     * **数据通道密钥** = SHA256(数据密码)（与 cbs-pair 通道同一把）。
     *
     * 用户要求：只有**话筒喊话**需要 6 位动态码，播控/排程/调休"连上就能用"。
     * 手机到不了电脑的 TCP 端口（没建虚拟网卡），所以数据也必须走 UDP 加密包；
     * 于是约定：`sid = 0` 的 TypeApi 包用**数据密码派生的密钥**（二维码里就带着），
     * 电脑端只要看到"来源在虚拟网内 + 能解开这层 AEAD"就受理 —— 不需要配对。
     */
    var dataKey: ByteArray = ByteArray(0)

    /** 当前该用哪个会话号/密钥：配音了就用会话，否则用数据通道。 */
    private fun useSession(): Boolean = sid > 0 && sessionKey.size == 32
    private fun activeSid(): Int = if (useSession()) sid else 0
    private fun activeKey(): ByteArray = if (useSession()) sessionKey else dataKey

    /** 有没有可用的通道（配对或数据密码任一即可）。 */
    val canCall: Boolean get() = activeKey().size == 32

    var lastError: String = ""
    var calls = 0; var oks = 0; var errors = 0

    /** 默认超时：中继一次往返通常 <1s，给到 8s 容忍抖动。 */
    var timeoutMs = 8000

    /**
     * 接口流的序号。
     *
     * 【会话通道】必须与音频帧**共用同一个递增空间**：电脑端按会话统一校验序号（防重放窗口）。
     * 以前音频从 0 开始、接口从 0x40000000 开始，两组序号来回跳 → 接口那几个大序号一过，
     * 之后所有音频帧都被判成"重放"丢弃（真机现象：手机显示已发上千帧，电脑端 `Ogg 0 页`、
     * 播放器从未启动，状态里 `重放` 数字一直涨）。所以会话通道两边都取 `nextSessionSeq()`。
     *
     * 【数据通道（sid=0）】**不能**用会话计数器：它的密钥是由数据密码派生的**静态密钥**
     * （所有手机共用一把），若 seq 每次启动都从同一处开始就会 AES-GCM nonce 复用。
     * 这里改用**每安装持久化的 64 位单调计数器**（MicProtocol.DataSeqStore，
     * 由 AppState 注入 SharedPreferences 落盘）。4.0.0 起 seq 是 8 字节，两种计数器都装得下。
     */
    private fun apiSeq(): Long =
        if (useSession()) MicProtocol.nextSessionSeq()
        else MicProtocol.DataSeqStore.next()

    private fun nextId(): String = "a" + seq.incrementAndGet() + "-" + (System.currentTimeMillis() % 100000)

    /** 本机设备 ID：随每个请求带给电脑端，用于"只有管理员设备才能改排程/调休"的判定。 */
    var deviceId: String = ""
    /** 设备凭证（类 cookie）：配对时由电脑端下发，之后每个请求都带上。 */
    var credential: String = ""
    /**
     * 机型名（给电脑端「管理员」窗口显示，便于认设备）。
     *
     * 【为什么默认就给机型】以前这个字段只有扫码配置里带 `mic.name` 时才有值，
     * 而二维码里根本不写 name —— 于是**永远为空**，`dname` 从不上报，
     * 电脑端只能退回去显示 `device_id`（32 位 UUID），人类根本没法认。
     * 现在默认取 `厂商 + 型号`（如 "Xiaomi Redmi K40"），用户不用配置也能一眼认出。
     */
    var deviceName: String = defaultDeviceName()
    /** 最近一次写操作的信封摘要（诊断用，日志导出时会带上）。 */
    var writeTrace: String = ""

    /** 同步调用一个接口；[body] 传 JSON 字符串（POST 用）。 */
    fun call(method: String, path: String, body: String? = null, timeout: Int = timeoutMs): Result {
        val key = activeKey()
        if (key.size != 32) {
            return Result(false, 0, "", "还没有可用通道：等入网，或让电脑端在「vnt 设置」里填数据密码（二维码要重扫）")
        }
        val id = nextId()
        val req = JSONObject().apply {
            put("v", 1)
            put("id", id)
            put("m", method)
            put("p", path)
            if (deviceId.isNotBlank()) put("did", deviceId)      // 管理员判定用
            if (credential.isNotBlank()) put("cred", credential)  // 设备凭证（类 cookie）
            if (deviceName.isNotBlank()) put("dname", deviceName)  // 机型名（管理员窗口里认设备用）
            // 载荷可能是**对象**（播控指令/任务）也可能是**数组**（调休整表）——
            // 以前一律 JSONObject(body)，数组会直接抛异常 → "调休保存失败"（真 bug）。
            if (body != null) put("b", org.json.JSONTokener(body).nextValue())
        }.toString()
        // 写操作留一条"信封摘要"（诊断"路径为空"这类问题：一眼看出 m/p 是否真的带上了）
        if (method != "GET") writeTrace = "m=" + method + " p=" + path + " 长度=" + req.length

        // ---- 可靠性：UDP 会丢包，所以同一请求（id 不变）自动重传，最多 3 次、逐次放宽超时 ----
        // 电脑端按 id 去重：重传只会把上次的答复再发一遍，**不会重复执行**写操作。
        val plan = if (timeout >= 6000) intArrayOf(timeout, timeout, timeout)          // 调用方已给足时间就不再翻倍
                   else intArrayOf(timeout, timeout + 2000, timeout + 4000)
        var lastErrText = ""
        for (attempt in plan.indices) {
            val latch = CountDownLatch(1)
            val box = arrayOfNulls<Result>(1)
            waiting[id] = Pair(latch, box)
            calls++
            try {
                // 大请求（整表提交这类）也要分片，否则一样会因 IP 分片丢包而失败
                sendMaybeChunked(req, id, key, activeSid())
                if (!latch.await(plan[attempt].toLong(), TimeUnit.MILLISECONDS)) {
                    lastErrText = "接口超时（${plan[attempt]}ms，第 ${attempt + 1}/${plan.size} 次）"
                    if (attempt < plan.lastIndex) { retries++; lastError = "接口超时（${plan[attempt]}ms），正在重传（第 ${attempt + 2} 次）"; continue }
                    errors++
                    lastError = "$lastErrText：电脑端没回包（丢包/中继不通/接口太慢）——重传 ${plan.size} 次仍未成功"
                    return Result(false, 0, "", lastError)
                }
                val r = box[0] ?: Result(false, 0, "", "响应为空")
                if (r.ok) oks++ else errors++
                if (attempt > 0) retriesRecovered++      // 重传救回来的请求数（可靠性指标）
                return r
            } catch (t: Throwable) {
                errors++
                lastError = "接口调用异常：" + (t.message ?: t.javaClass.simpleName)
                return Result(false, 0, "", lastError)
            } finally {
                waiting.remove(id)
            }
        }
        return Result(false, 0, "", lastErrText)
    }

    /**
     * 收到一个 TypeApi 包时调用（由 vnt-lite 的收包回调转进来）。
     * 返回 true 表示这是一个接口响应、已被消费。
     */
    fun onPacket(data: ByteArray): Boolean {
        // 会话密钥（配音了）或数据通道密钥，哪个能解开就用哪个 —— AEAD tag 决定只有一种能过
        var opened = MicProtocol.tryOpenPacket(data, activeKey())
        if (opened.first == null && dataKey.size == 32 && sessionKey.size == 32 &&
            !sessionKey.contentEquals(dataKey)) {
            opened = MicProtocol.tryOpenPacket(data, dataKey)
        }
        val pkt = opened.first
        if (pkt == null) {
            lastError = "接口响应解不开：${opened.second}"
            return true
        }
        if (pkt.type != MicProtocol.TYPE_API) return false
        val text = String(pkt.payload, Charsets.UTF_8)
        // 分片响应：攒齐再当响应处理（大响应在公网上会 IP 分片，丢一片整包就没了）
        val joined = joinChunk(text)
        if (joined == null) return true
        deliver(joined)
        return true
    }

    /** 处理一条完整的接口响应文本（分片拼齐后也走这里）。 */
    private fun deliver(text: String) {
        val obj = try { JSONObject(text) } catch (e: Exception) {
            lastError = "接口响应不是 JSON"
            return
        }
        val id = obj.optString("id")
        val errMsg = if (obj.has("e")) obj.optString("e") else null
        val status = obj.optInt("s", 0)
        val body = obj.optString("d")
        val res = if (errMsg != null) Result(false, 0, "", errMsg) else Result(true, status, body, null)
        val w = waiting[id]
        if (w != null) {
            w.second[0] = res
            w.first.countDown()
        } else {
            lastError = "收到无法匹配的接口响应（id=$id，可能已超时）"
        }
    }

    // ================= 分片（防 IP 分片丢包：真机"拉取失败率太高"的根因） =================

    /** 超过这个字节数就压缩 + 分片。 */
    private val chunkThreshold = 3500

    /** 每片 base64 字符数（解码后约 600 字节，远小于 MTU）。 */
    private val chunkDataLen = 800

    private val inChunks = ConcurrentHashMap<String, Array<String?>>()
    private val inChunkAt = ConcurrentHashMap<String, Long>()

    /** 发一个请求：小就直接发，大就 gzip + base64 + 分片发。 */
    private fun sendMaybeChunked(json: String, id: String, key: ByteArray, sid: Int) {
        val raw = json.toByteArray(Charsets.UTF_8)
        if (raw.size <= chunkThreshold) {
            sender(MicProtocol.buildPacket(MicProtocol.TYPE_API, sid, apiSeq(), 0, raw, key))
            return
        }
        val bos = java.io.ByteArrayOutputStream()
        java.util.zip.GZIPOutputStream(bos).use { it.write(raw) }
        val b64 = android.util.Base64.encodeToString(bos.toByteArray(), android.util.Base64.NO_WRAP)
        val total = (b64.length + chunkDataLen - 1) / chunkDataLen
        for (i in 0 until total) {
            val piece = b64.substring(i * chunkDataLen, minOf((i + 1) * chunkDataLen, b64.length))
            val env = JSONObject().apply {
                put("v", 1); put("id", id); put("z", 1); put("i", i); put("n", total); put("d", piece)
            }.toString()
            sender(MicProtocol.buildPacket(MicProtocol.TYPE_API, sid, apiSeq(), 0, env.toByteArray(Charsets.UTF_8), key))
        }
        chunksSent += total
    }

    /**
     * 若 [text] 是一片，就攒起来：全齐返回**完整 JSON**，没齐返回 null，不是分片就原样返回 [text]。
     * 信封：{"v":1,"id":"…","z":1,"i":<第几片>,"n":<共几片>,"d":"<base64 片段>"}
     */
    private fun joinChunk(text: String): String? {
        val o = try { JSONObject(text) } catch (e: Exception) { return text }
        if (!o.has("n") || !o.has("i") || !o.has("d")) return text
        val id = o.optString("id")
        val n = o.optInt("n"); val i = o.optInt("i"); val d = o.optString("d")
        if (n <= 0 || i < 0 || i >= n) { lastError = "接口响应分片参数不合法（i=$i n=$n）"; return null }
        val now = System.currentTimeMillis()
        val arr = inChunks[id]
        val parts = if (arr != null && arr.size == n && now - (inChunkAt[id] ?: 0L) < 20000) arr
                    else arrayOfNulls<String>(n).also { inChunks[id] = it; chunksReceived++ }
        parts[i] = d
        inChunkAt[id] = now
        for (k in 0 until n) if (parts[k] == null) return null
        inChunks.remove(id); inChunkAt.remove(id)
        if (inChunks.size > 32) { inChunks.clear(); inChunkAt.clear() }
        return try {
            val gz = android.util.Base64.decode(parts.joinToString(""), android.util.Base64.NO_WRAP)
            val ins = java.util.zip.GZIPInputStream(gz.inputStream())
            val out = ins.readBytes()
            ins.close()
            String(out, Charsets.UTF_8)
        } catch (t: Throwable) {
            lastError = "接口响应分片解压失败：" + (t.message ?: t.javaClass.simpleName)
            null
        }
    }

    /** 诊断计数（导出日志里能看到"分片/重传到底有没有在工作"）。 */
    var chunksSent = 0
    var chunksReceived = 0
    /** 因超时重传的次数（UDP 丢包的直观指标）。 */
    var retries = 0
    /** 靠重传救回来的请求数（可靠性指标：这些请求第一次是丢了的）。 */
    var retriesRecovered = 0

    companion object {
        /**
         * 默认机型名：`厂商 + 型号`，并去掉重复前缀。
         *
         * 例：Xiaomi 的 `Build.MODEL` 通常已经是 "Redmi K40"，直接拼会变成
         * "Xiaomi Redmi K40"（可读 ✓）；但有些厂商的 MODEL 以厂商名开头（"Xiaomi M2101K9C"），
         * 这时不再重复拼厂商。取不到就退回 "Android"，保证电脑端永远有东西显示、不会退化成 UUID。
         */
        fun defaultDeviceName(): String {
            return try {
                val mfr = android.os.Build.MANUFACTURER?.trim().orEmpty()
                val mdl = android.os.Build.MODEL?.trim().orEmpty()
                when {
                    mdl.isBlank() && mfr.isBlank() -> "Android"
                    mdl.isBlank() -> mfr
                    mfr.isBlank() -> mdl
                    mdl.startsWith(mfr, ignoreCase = true) -> mdl
                    else -> "$mfr $mdl"
                }
            } catch (t: Throwable) { "Android" }
        }
    }
}
