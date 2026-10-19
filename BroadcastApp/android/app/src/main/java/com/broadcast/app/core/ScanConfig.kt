package com.broadcast.app.core

import org.json.JSONObject

/**
 * 扫一扫解析出来的连接配置（对应电脑端 `VntManager.BuildQrPayload` 生成的 JSON）。
 *
 * 电脑端二维码原文形状（字段名以它为准）：
 * ```json
 * {
 *   "t": "cbs-mic", "v": 1,
 *   "vnt": { "server": "host:port", "token": "…", "device_id": "…", "protocol": "udp",
 *            "model": "aes_gcm", "finger": true, "w_password": "…" },
 *   "mic": { "host": "10.26.0.1", "port": 29880, "ctl_port": 29881,
 *            "http_port": 5000, "name": "教学楼广播主机", "code_hint": "…" }
 * }
 * ```
 */
data class ScannedConfig(
    val server: String,
    val token: String,
    val deviceId: String,
    val protocol: String,
    val model: String,
    val finger: Boolean,
    val wPassword: String,
    val micHost: String,
    val audioPort: Int,
    val ctlPort: Int,
    val httpPort: Int,
    val deviceName: String,
    /**
     * 电脑端在虚拟网里的地址（`mic.vnt_ip`）= **程序内置 vnt-lite** 的地址（和手机同 token、同服务器）。
     * 手机不建虚拟网卡，配对包与音频包要**经 vnt-lite 中继**发到这里 —— 这是唯一可靠的通道。
     */
    val vntIp: String = "",
    /**
     * 电脑端的局域网地址（`mic.lan_ip`）：手机唯一能用**裸 UDP 直发**的地址（10.x 它没有路由）。
     * 拿不到就是空串，此时只走 vnt 中继。
     */
    val lanIp: String = ""
) {
    /** 二维码里没带数据密码（UDP 配对通道会不可用，界面要提醒）。 */
    val dataPasswordBlank: Boolean get() = wPassword.isBlank()
}

object ScanConfigParser {
    /** 解析失败时返回 null，并在 [error] 里给出人话原因。 */
    fun parse(text: String, error: (String) -> Unit = {}): ScannedConfig? {
        val raw = text.trim()
        if (raw.isEmpty()) { error("内容为空"); return null }
        val json = try { JSONObject(raw) } catch (e: Exception) {
            error("不是合法的 JSON（可能扫到了别的二维码）"); return null
        }
        if (json.optString("t") != "cbs-mic") {
            error("不是本程序的二维码（t=${json.optString("t")}）"); return null
        }
        val vnt = json.optJSONObject("vnt")
        val mic = json.optJSONObject("mic")
        if (vnt == null) { error("二维码缺少 vnt 段"); return null }
        val server = vnt.optString("server").trim()
        val token = vnt.optString("token").trim()
        if (server.isEmpty()) { error("二维码里没有服务器地址（电脑端还没填）"); return null }
        if (token.isEmpty()) { error("二维码里没有组网令牌（电脑端还没填）"); return null }
        return ScannedConfig(
            server = server,
            token = token,
            deviceId = vnt.optString("device_id").trim(),
            protocol = vnt.optString("protocol", "udp").ifBlank { "udp" },
            model = vnt.optString("model", "aes_gcm").ifBlank { "aes_gcm" },
            finger = vnt.optBoolean("finger", false),
            wPassword = vnt.optString("w_password"),
            micHost = mic?.optString("host")?.trim().orEmpty(),
            audioPort = mic?.optInt("port", 29880) ?: 29880,
            ctlPort = mic?.optInt("ctl_port", 29881) ?: 29881,
            httpPort = mic?.optInt("http_port", 5000) ?: 5000,
            deviceName = mic?.optString("name")?.trim().orEmpty(),
            vntIp = mic?.optString("vnt_ip")?.trim().orEmpty(),
            lanIp = mic?.optString("lan_ip")?.trim().orEmpty()
        )
    }
}
