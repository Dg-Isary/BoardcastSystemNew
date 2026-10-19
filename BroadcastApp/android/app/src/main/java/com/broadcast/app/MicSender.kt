package com.broadcast.app

import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaFormat
import android.media.MediaRecorder
import com.broadcast.vntlite.MicProtocol
import java.nio.ByteBuffer

/**
 * 麦克风采集 → Opus 编码 → Ogg 页 → MicProtocol AEAD 包 → 交给 vnt-lite 发送。
 * 20ms 一帧（48kHz 单声道 960 采样）；Opus 由系统 MediaCodec 提供（Android 10+ 普遍内置）。
 *
 * [sid] **必须是电脑端 AUTH_OK 返回的会话号**：电脑端 `MicServer` 按 sid 查会话，
 * 会话不存在会直接丢弃（表现为"配对成功但没声音"）。以前这里用当前时间当 sid，
 * 结果所有音频包都因为"未知会话"被丢弃——这是手机端必须先修的一处 bug。
 */
class MicSender(private val sid: Int, private val key: ByteArray, private val onPacket: (ByteArray) -> Unit) {
    private val sampleRate = 48000
    private val frameSamples = 960                      // 20ms
    private var codec: MediaCodec? = null
    private var thread: Thread? = null
    @Volatile private var running = false
    private val ogg = OggWriter(sampleRate, 1)
    private var seq = 0
    var sentFrames = 0; private set
    var lastError = ""; private set

    /** 本次喊话是否把 OpusHead 页发出去了（漏发会导致电脑端 ffplay 不出声，日志能查）。 */
    var headSent = false; private set

    fun start(): Pair<Boolean, String?> {
        if (sid <= 0) return Pair(false, "会话号无效（sid=$sid）：请先点「配对（输动态码）」完成配对")
        if (key.size != 32) return Pair(false, "会话密钥长度不对（${key.size} 字节，应为 32）：请重新配对")
        // PCM 来自 PcmMixer（麦克风 / 系统声音两个独立开关，可同时开=混音）；
        // 这里只负责 Opus 编码与发包，不再自己创建 AudioRecord。
        if (!PcmMixer.micRunning && !PcmMixer.sysRunning)
            return Pair(false, "没有打开任何采集源：请至少打开「麦克风」或「系统声音」")
        val fmt = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, sampleRate, 1).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, 24000)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4096)
        }
        val enc = try { MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS) }
        catch (e: Exception) { return Pair(false, "系统没有 Opus 编码器（Android 10+ 才有）：" + e.message) }
        try {
            enc.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            enc.start()
        } catch (e: Exception) { enc.release(); return Pair(false, "Opus 编码器配置失败：" + e.message) }
        codec = enc; running = true
        onPacket(MicProtocol.buildPacket(MicProtocol.TYPE_OGG, sid, MicProtocol.nextSessionSeq(), 20, ogg.opusHead(), key))   // 先发 OpusHead 页
        // **必须紧跟 OpusTags**：ffmpeg 的 ogg 解复用器要求 BOS 页之后是注释头，缺了它会把第一个音频包
        // 当注释头解析并直接报错退出（真机现象：播放中途 ffplay 退出，重启后没有 OpusHead → 彻底无声）。
        onPacket(MicProtocol.buildPacket(MicProtocol.TYPE_OGG, sid, MicProtocol.nextSessionSeq(), 20, ogg.opusTags(), key))
        headSent = true
        thread = Thread { loop() }.also { it.isDaemon = true; it.start() }
        return Pair(true, null)
    }

    private fun loop() {
        val bufInfo = MediaCodec.BufferInfo()
        val pcm = ByteArray(frameSamples * 2)
        val mix = ShortArray(frameSamples)
        while (running) {
            try {
                // PCM 从混音器取（麦克风 / 系统声音 两个独立开关，可同时开=混音）
                if (!PcmMixer.nextFrame(mix)) { Thread.sleep(3); continue }
                for (i in 0 until frameSamples) {
                    val v = mix[i].toInt()
                    pcm[i * 2] = (v and 0xFF).toByte()
                    pcm[i * 2 + 1] = ((v ushr 8) and 0xFF).toByte()
                }
                val enc = codec ?: break
                val inIdx = enc.dequeueInputBuffer(10000)
                if (inIdx >= 0) {
                    val inBuf: ByteBuffer = enc.getInputBuffer(inIdx)!!
                    inBuf.clear(); inBuf.put(pcm)
                    enc.queueInputBuffer(inIdx, 0, pcm.size, System.nanoTime() / 1000, 0)
                }
                var outIdx = enc.dequeueOutputBuffer(bufInfo, 10000)
                while (outIdx >= 0) {
                    val outBuf: ByteBuffer = enc.getOutputBuffer(outIdx)!!
                    val oggPage = ByteArray(bufInfo.size)
                    outBuf.position(bufInfo.offset); outBuf.get(oggPage, 0, bufInfo.size)
                    if (bufInfo.size > 0) {
                        val page = ogg.opusPage(oggPage)
                        onPacket(MicProtocol.buildPacket(MicProtocol.TYPE_OGG, sid, MicProtocol.nextSessionSeq(), 20, page, key))
                        sentFrames++
                    }
                    enc.releaseOutputBuffer(outIdx, false)
                    outIdx = enc.dequeueOutputBuffer(bufInfo, 0)
                }
            } catch (e: Exception) { lastError = "采集/编码异常：" + e.message; break }
        }
    }

    fun stop() {
        running = false
        try { thread?.join(500) } catch (_: Exception) {}
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        codec = null
        // 采集源由 PcmMixer 管（麦克风/系统声音各自的开关决定是否继续采），这里不释放它们
    }
}
