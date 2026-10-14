package com.broadcast.app

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.projection.MediaProjection
import android.util.Log
import java.util.concurrent.ArrayBlockingQueue

/**
 * 采集源与混音器（48kHz 单声道 s16le，每帧 960 样本 = 20ms）。
 *
 * 用户要求：**麦克风与系统声音做成两个独立开关**，各自可单独开，也可同时开（同时开 = 混音后一起送）。
 *
 * 为什么这样分层：MicSender 只负责"编码 + 发包"，PCM 从 [PcmMixer] 取；
 * 每种采集源自己一个线程把 20ms 帧塞进队列，混音器按帧求和（带限幅），
 * 于是"开一个/开两个/都不开"都不需要动编码与发送逻辑。
 *
 * 系统声音用 Android 10+ 的 AudioPlaybackCapture（需要 RECORD_AUDIO + MediaProjection 授权，
 * 且对方 App 可以声明"不允许被采集"，通话语音/DRM 音频系统一律不给 —— 这些都是系统限制）。
 */
object PcmMixer {

    const val SAMPLE_RATE = 48000
    const val FRAME_SAMPLES = 960          // 20ms
    private const val QUEUE = 24           // 每源最多缓冲 24 帧（约 0.5 秒），满了丢最旧的

    private val micQ = ArrayBlockingQueue<ShortArray>(QUEUE)
    private val sysQ = ArrayBlockingQueue<ShortArray>(QUEUE)

    @Volatile var micRunning = false; private set
    @Volatile var sysRunning = false; private set

    private var micRec: AudioRecord? = null
    private var micThread: Thread? = null
    private var sysRec: AudioRecord? = null
    private var sysThread: Thread? = null

    /** 最近一次错误（界面/日志用）。 */
    @Volatile var lastError = ""

    // ---------------- 麦克风 ----------------

    /** 打开麦克风采集。返回 (是否成功, 错误说明)。 */
    fun startMic(context: Context): Pair<Boolean, String?> {
        if (micRunning) return Pair(true, null)
        val ok = context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        if (!ok) return Pair(false, "没有录音权限：请到「设置 → 应用 → 广播系统手控端 → 权限」里允许录音")
        val minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuf <= 0) return Pair(false, "麦克风不可用（getMinBufferSize=$minBuf）")
        val rec = try {
            AudioRecord(MediaRecorder.AudioSource.MIC, SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, minBuf * 4)
        } catch (e: Exception) { return Pair(false, "创建 AudioRecord 失败：" + e.message) }
        if (rec.state != AudioRecord.STATE_INITIALIZED) { rec.release(); return Pair(false, "麦克风未初始化") }
        micQ.clear(); micRec = rec; rec.startRecording(); micRunning = true
        micThread = Thread { pumpMono(rec, micQ) { micRunning } }.also { it.isDaemon = true; it.name = "pcm-mic"; it.start() }
        return Pair(true, null)
    }

    fun stopMic() {
        micRunning = false
        try { micRec?.stop() } catch (_: Throwable) { }
        try { micRec?.release() } catch (_: Throwable) { }
        micRec = null; micQ.clear()
    }

    // ---------------- 系统声音 ----------------

    /**
     * 打开系统声音采集（需要 MediaProjection 授权）。
     * @param projection 由界面弹系统框拿到（见 MainActivity / AppState）
     */
    fun startSystem(projection: MediaProjection?): Pair<Boolean, String?> {
        if (sysRunning) return Pair(true, null)
        if (projection == null) return Pair(false, "没有屏幕录制授权（系统框被取消）：请再点一次开关并允许")
        try {
            val cfg = android.media.AudioPlaybackCaptureConfiguration.Builder(projection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()
            val fmt = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(SAMPLE_RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)     // 采集侧固定立体声，这里自己降成单声道
                .build()
            val minBuf = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            val rec = AudioRecord.Builder()
                .setAudioFormat(fmt)
                .setBufferSizeInBytes(maxOf(minBuf * 4, 48000))
                .setAudioPlaybackCaptureConfig(cfg)
                .build()
            if (rec.state != AudioRecord.STATE_INITIALIZED) { rec.release(); return Pair(false, "系统声音采集不可用（设备/系统不支持内录）") }
            sysQ.clear(); sysRec = rec; rec.startRecording(); sysRunning = true
            sysThread = Thread { pumpStereoToMono(rec, sysQ) { sysRunning } }.also { it.isDaemon = true; it.name = "pcm-sys"; it.start() }
            return Pair(true, null)
        } catch (e: Throwable) {
            lastError = "系统声音采集失败：" + (e.message ?: e.javaClass.simpleName)
            Log.w("PcmMixer", lastError, e)
            return Pair(false, lastError)
        }
    }

    fun stopSystem() {
        sysRunning = false
        try { sysRec?.stop() } catch (_: Throwable) { }
        try { sysRec?.release() } catch (_: Throwable) { }
        sysRec = null; sysQ.clear()
    }

    fun stopAll() { stopMic(); stopSystem() }

    // ---------------- 混音输出 ----------------

    /** 发送音量（数字增益，0.2~2.0）。界面滑条与手机**物理音量键**都调它。 */
    @Volatile var gain: Float = 1.0f

    /**
     * 给编码器取一帧（960 样本单声道，20ms）。
     * 取到返回 true；两种源都没数据（比如都关了或刚启动还没采到）返回 false，调用方跳过这一帧即可。
     */
    fun nextFrame(out: ShortArray): Boolean {
        val a = if (micRunning) micQ.poll() else null
        val b = if (sysRunning) sysQ.poll() else null
        val src = a ?: b ?: return false
        val g = gain
        val n = minOf(out.size, src.size)
        for (i in 0 until n) {
            var v = src[i].toInt()
            if (a != null && b != null) v += b[i % b.size].toInt()      // 同时开：两路相加
            if (g != 1.0f) v = (v * g).toInt()                          // 音量（数字增益）
            if (v > 32767) v = 32767 else if (v < -32768) v = -32768     // 限幅，防削顶爆音
            out[i] = v.toShort()
        }
        return true
    }

    // ---------------- 采集线程 ----------------

    private fun pumpMono(rec: AudioRecord, q: ArrayBlockingQueue<ShortArray>, alive: () -> Boolean) {
        val buf = ShortArray(FRAME_SAMPLES)
        while (alive()) {
            try {
                var read = 0
                while (read < buf.size && alive()) {
                    val n = rec.read(buf, read, buf.size - read)
                    if (n <= 0) break
                    read += n
                }
                if (read < buf.size) { Thread.sleep(2); continue }
                if (!q.offer(buf.copyOf())) { q.poll(); q.offer(buf.copyOf()) }   // 满了丢最旧，绝不阻塞采集
            } catch (t: Throwable) {
                if (!alive()) break
                lastError = "麦克风采集异常：" + (t.message ?: t.javaClass.simpleName)
                Thread.sleep(20)
            }
        }
    }

    private fun pumpStereoToMono(rec: AudioRecord, q: ArrayBlockingQueue<ShortArray>, alive: () -> Boolean) {
        val inter = ShortArray(FRAME_SAMPLES * 2)                              // 立体声交错 LRLR…
        val mono = ShortArray(FRAME_SAMPLES)
        while (alive()) {
            try {
                var read = 0
                while (read < inter.size && alive()) {
                    val n = rec.read(inter, read, inter.size - read)
                    if (n <= 0) break
                    read += n
                }
                if (read < inter.size) { Thread.sleep(2); continue }
                for (i in 0 until FRAME_SAMPLES) {
                    val l = inter[i * 2].toInt(); val r = inter[i * 2 + 1].toInt()
                    mono[i] = ((l + r) / 2).toShort()                          // L/R 平均降成单声道
                }
                if (!q.offer(mono.copyOf())) { q.poll(); q.offer(mono.copyOf()) }
            } catch (t: Throwable) {
                if (!alive()) break
                lastError = "系统声音采集异常：" + (t.message ?: t.javaClass.simpleName)
                Thread.sleep(20)
            }
        }
    }
}
