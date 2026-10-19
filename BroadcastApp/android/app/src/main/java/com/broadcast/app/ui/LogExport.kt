package com.broadcast.app.ui

import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.content.FileProvider
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 手机端日志导出：把"界面日志 + 关键状态 + 配置摘要"写成一个 txt，然后走系统分享
 * （可以直接发到微信「文件传输助手」，一步到电脑上，不用再手打粘贴）。
 *
 * 位置：`<App 私有目录>/files/logs/cbs-log-<时间>.txt`（不需要任何存储权限）。
 * 分享用 FileProvider（manifest 里已声明 `${applicationId}.fileprovider`）。
 */
object LogExport {

    private const val DIR = "logs"

    /** 生成要导出的文本：脱敏后的配置 + 运行状态 + 全部界面日志。 */
    fun build(state: AppState): String {
        val sb = StringBuilder()
        val now = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
        sb.append("==== 广播系统手控端 · 排错日志 ====\n")
        sb.append("导出时间：").append(now).append('\n')
        sb.append("版本：v").append(APP_VERSION).append("（构建 ").append(APP_BUILD).append("）\n")
        sb.append("机型：").append(Build.MANUFACTURER).append(' ').append(Build.MODEL)
            .append("　Android ").append(Build.VERSION.RELEASE).append("（API ").append(Build.VERSION.SDK_INT).append("）\n")
        sb.append("ABI：").append(Build.SUPPORTED_ABIS.joinToString(",")).append("\n\n")

        sb.append("---- 运行状态 ----\n")
        sb.append("已配置：").append(state.configured).append("　已入网：").append(state.registered)
            .append("　组网客户端在跑：").append(state.connected).append('\n')
        sb.append("我的虚拟IP：").append(state.myVirtualIp.ifBlank { "-" }).append('\n')
        sb.append("自动重连次数：").append(state.reconnectCount).append('\n')
        sb.append("状态行：").append(state.statusLine).append('\n')
        sb.append("最近错误：").append(state.lastError.ifBlank { "-" }).append('\n')
        sb.append("配对：").append(if (state.paired) "已配对 sid=${state.pairingSid}" else "未配对")
            .append("　配对待机：").append(state.pairing).append('\n')
        sb.append("话筒：").append(state.micStatus).append("　已发帧：").append(state.micFrames).append('\n')
        if (state.pcAudioHealth.isNotBlank()) sb.append("电脑端音频：").append(state.pcAudioHealth).append('\n')
        sb.append("数据：").append(state.dataStatus).append("　").append(state.apiChunkStats).append('\n')
        state.vnt?.let { c ->
            sb.append("vnt-lite：").append(runCatching { c.statusText() }.getOrDefault("-")).append('\n')
            sb.append("vnt 收/发包：").append(c.recvPackets).append('/').append(c.sentPackets)
                .append("　对端包 收/发：").append(c.peerPacketsReceived).append('/').append(c.peerPacketsSent).append('\n')
            val le = runCatching { c.lastError }.getOrDefault("")
            if (!le.isNullOrBlank()) sb.append("vnt 最近错误：").append(le).append('\n')
        }
        sb.append('\n')

        sb.append("---- 连接配置（密钥已脱敏）----\n")
        sb.append("服务器：").append(state.vntServer.ifBlank { "-" }).append('\n')
        sb.append("组网令牌：").append(mask(state.vntToken)).append('\n')
        sb.append("数据密码：").append(mask(state.dataPassword)).append('\n')
        sb.append("协议/加密：").append(state.protocol).append(" / ").append(state.model)
            .append("　指纹：").append(state.useFinger).append("　中继优先：").append(state.relayFirst).append('\n')
        sb.append("电脑端虚拟IP：").append(state.pcVntIp.ifBlank { "-" })
            .append("　实测可用：").append(if (state.activePcVip != 0) com.broadcast.vntlite.VntPacket.intToIp(state.activePcVip) else "-").append('\n')
        sb.append("电脑端局域网IP：").append(state.pcLanIp.ifBlank { "-" }).append('\n')
        sb.append("端口：音频 ").append(state.audioPort).append("　配对 ").append(state.ctlPort)
            .append("　网页 ").append(state.httpPort).append('\n')
        sb.append("主机名：").append(state.hostName.ifBlank { "-" }).append('\n')
        sb.append("本机设备ID：").append(state.phoneDeviceId).append("（固定不变）\n\n")

        sb.append("---- 操作日志（最多 300 条，新的在最后）----\n")
        if (state.logs.isEmpty()) sb.append("(空)\n")
        else state.logs.forEach { sb.append('[').append(it.time).append("] ").append(it.text).append('\n') }
        return sb.toString()
    }

    /** 写入私有目录，返回文件（失败抛异常，由调用方提示）。 */
    fun write(context: Context, text: String): File {
        val dir = File(context.filesDir, DIR).apply { mkdirs() }
        val name = "cbs-log-" + SimpleDateFormat("MMdd-HHmmss", Locale.US).format(Date()) + ".txt"
        val f = File(dir, name)
        f.writeText(text, Charsets.UTF_8)
        return f
    }

    /** 走系统分享（微信/QQ/邮件/文件管理器都行）。 */
    fun share(context: Context, file: File) {
        val uri = FileProvider.getUriForFile(context, context.packageName + ".fileprovider", file)
        val intent = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_STREAM, uri)
            putExtra(Intent.EXTRA_SUBJECT, file.name)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val chooser = Intent.createChooser(intent, "把排错日志发到电脑（微信文件传输助手 / 邮件）").apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(chooser)
    }

    private fun mask(s: String): String = when {
        s.isBlank() -> "-"
        s.length <= 4 -> "****"
        else -> s.take(2) + "****" + s.takeLast(2)
    }
}
