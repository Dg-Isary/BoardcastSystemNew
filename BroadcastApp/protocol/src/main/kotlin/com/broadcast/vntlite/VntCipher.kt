package com.broadcast.vntlite

import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * vnt 客户端数据加密 —— 与 C# 侧（VntLite\VntCipher.cs）逐字节一致，依据 vnt 1.2.16 源码：
 *  · 密钥 = SHA256(密码)；密码长度 < 8 时取前 16 字节（AES-128），否则 32 字节（AES-256）
 *  · 指纹哈希 = SHA256(token)
 *  · AEAD = AES-GCM，**AAD 为空**；nonce(12) = 源IP(4B 大端) ‖ 目的IP(4B 大端) ‖ 外层协议 ‖ 上层协议 ‖ 网关标志 ‖ 源TTL
 *  · 载荷 = 密文 ‖ tag(16) ‖ random(4) ‖ finger(12)；finger = SHA256(nonce ‖ (密文‖tag‖random) ‖ SHA256(token))[20..32]
 *  · 头字节 e 位（0x80）必须置 1，否则对端/服务端按 "not encrypt" 丢弃
 */
object VntCipher {
    const val TAG_LEN = 16
    const val RANDOM_LEN = 4
    const val FINGER_LEN = 12
    const val RESERVED = TAG_LEN + RANDOM_LEN + FINGER_LEN   // 32
    private val rng = SecureRandom()

    fun sha256(vararg parts: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        for (p in parts) md.update(p)
        return md.digest()
    }

    /** 数据密钥：SHA256(密码)；短于 8 字符时取前 16 字节（与上游一致） */
    fun deriveKey(password: String?): ByteArray {
        val full = sha256((password ?: "").toByteArray(Charsets.UTF_8))
        return if ((password ?: "").length < 8) full.copyOfRange(0, 16) else full
    }

    fun fingerHash(token: String?): ByteArray = sha256((token ?: "").toByteArray(Charsets.UTF_8))

    fun buildNonce(p: VntPacket): ByteArray {
        val n = ByteArray(12)
        VntPacket.writeU32(n, 0, p.srcIp)
        VntPacket.writeU32(n, 4, p.dstIp)
        n[8] = p.outer.toByte()
        n[9] = p.inner.toByte()
        n[10] = if (p.gateway) 1 else 0
        n[11] = p.initTtl.toByte()
        return n
    }

    fun computeFinger(nonce: ByteArray, body: ByteArray, fingerHash: ByteArray): ByteArray =
        sha256(nonce, body, fingerHash).copyOfRange(20, 32)

    /** 加密：返回 密文‖tag‖finger（useFinger=false 时不含 finger）。
     *
     * 与服务端源码 `SecretBody` 完全一致的关键点（之前所有实现都错在这里）：
     *   **random(4) 不是附加在密文后的明文字段，而是被加密的明文本身的一部分**。
     *   服务端 `encrypt_ipv4` 的顺序是 `set_random()` → `encrypt_in_place_detached(nonce, body_mut())`，
     *   其中 `body()` = `buffer[0..len-16-4]`（**含那 4 字节 random**）。所以 AES-GCM 的明文是
     *   `data ‖ random4`，解密侧要拿到原始 `data` 必须丢弃末尾 4 字节。
     */
    fun encrypt(plaintext: ByteArray, key: ByteArray, nonce: ByteArray, fingerHash: ByteArray, useFinger: Boolean): ByteArray {
        val rnd = ByteArray(RANDOM_LEN).also { rng.nextBytes(it) }
        // 明文 = data ‖ random4（与服务端 body() 的定义一致）
        val pt = plaintext + rnd
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LEN * 8, nonce))
        // 注意：AES-GCM 的 doFinal 返回的是【密文 ‖ tag(16)】，不要再额外加 tag 长度
        val ctTag = cipher.doFinal(pt)
        // body = 密文(ctTag) ‖ finger(12)（若 useFinger）；ctTag 里已含 tag(16)
        val extra = if (useFinger) FINGER_LEN else 0
        val body = ByteArray(ctTag.size + extra)
        System.arraycopy(ctTag, 0, body, 0, ctTag.size)
        if (useFinger) {
            val enBody = body.copyOfRange(0, ctTag.size)   // 密文‖tag（不含 finger）
            System.arraycopy(computeFinger(nonce, enBody, fingerHash), 0, body, ctTag.size, FINGER_LEN)
        }
        return body
    }

    /** 解密：先验 finger（启用时），再 AES-GCM；失败返回 null 并给出原因。
     *
     * 与 encrypt 对应的要点：`random4` 是明文的一部分（`data ‖ random4`），解密后**末尾 4 字节是 random，不是数据**。
     */
    fun tryDecrypt(payload: ByteArray, key: ByteArray, nonce: ByteArray, fingerHash: ByteArray, useFinger: Boolean): Pair<ByteArray?, String?> {
        val need = if (useFinger) TAG_LEN + FINGER_LEN else TAG_LEN
        if (payload.size < need) return Pair(null, "长度不足（至少 $need 字节）")
        val ctLen = payload.size - need - RANDOM_LEN        // 密文长度（= 明文数据长度，random 不计入）
        if (ctLen < 0) return Pair(null, "载荷过短")
        if (useFinger) {
            val enBody = payload.copyOfRange(0, ctLen + RANDOM_LEN + TAG_LEN)
            val want = computeFinger(nonce, enBody, fingerHash)
            val got = payload.copyOfRange(ctLen + RANDOM_LEN + TAG_LEN, payload.size)
            if (!want.contentEquals(got)) return Pair(null, "finger 校验失败")
        }
        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_LEN * 8, nonce))
            val pt = cipher.doFinal(payload, 0, ctLen + RANDOM_LEN + TAG_LEN)   // 密文‖tag 一起喂给 GCM
            Pair(pt.copyOfRange(0, ctLen), null)                               // 丢弃末尾 random4
        } catch (e: Exception) { Pair(null, "解密失败：" + e.message) }
    }
}