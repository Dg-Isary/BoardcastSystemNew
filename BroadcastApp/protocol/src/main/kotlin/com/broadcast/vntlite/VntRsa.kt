package com.broadcast.vntlite

import java.io.File
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import javax.crypto.Cipher

/**
 * vnt「服务端加密」（`-W`）用的 RSA 部分（与 C# 侧 `VntRsa.cs` 逐字节一致）。
 *
 * 关键点（两边源码已交叉印证）：
 *  · 客户端**必须**生成并持久化自己的 2048 位 RSA 密钥对，并在**第一次握手**里带上自己的公钥指纹
 *    （`HandshakeRequest.key_finger`）。服务端只在"两边指纹不同"时才回带公钥（server.rs L473-476）。
 *    指纹为空 = 客户端永远拿不到公钥 = 永远连不上。
 *  · 指纹 = SPKI(public_key_der) 的 SHA-256 → base64（无换行）。
 *  · 第二次握手：`SecretHandshakeRequest{token,key}`，**明文布局 = [protobuf][random16][finger16]**，
 *    其中 `finger = SHA256(body ‖ nonce12)[16..32]`，`body = [protobuf][random16]`。
 *  · RSA 加密用 **PKCS#1 v1.5**（`rsa::Pkcs1v15Encrypt`）。
 *  · 密钥对保存为**两个文件**：私钥 PKCS#8、公钥 SPKI（不要把两把钥匙写进同一个 PEM ——
 *    `ImportFromPem` 遇到多把密钥会直接抛异常，之前踩过这个坑）。
 */
object VntRsa {
    private val rng = SecureRandom()

    /** 生成并持久化本机密钥对（若已存在则直接读）。返回 (私钥, 公钥SPKI)。 */
    fun localKey(dir: File): Pair<PrivateKey, ByteArray> {
        val privFile = File(dir, "vnt_rsa_priv.der")
        val pubFile = File(dir, "vnt_rsa_pub.der")
        if (privFile.exists() && pubFile.exists()) {
            val privDer = privFile.readBytes()
            val pubDer = pubFile.readBytes()
            return Pair(
                KeyFactory.getInstance("RSA").generatePrivate(PKCS8EncodedKeySpec(privDer)),
                pubDer
            )
        }
        dir.mkdirs()
        val kpg = KeyPairGenerator.getInstance("RSA")
        kpg.initialize(2048, rng)
        val kp = kpg.generateKeyPair()
        val privDer = kp.private.encoded          // PKCS#8
        val pubDer = kp.public.encoded            // X.509 SPKI
        privFile.writeBytes(privDer)
        pubFile.writeBytes(pubDer)
        return Pair(kp.private, pubDer)
    }

    /** 本机公钥指纹 = SHA256(SPKI) 的 base64（无换行）。 */
    fun localFinger(pubSpki: ByteArray): String =
        Base64.getEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(pubSpki))

    /** 从服务端握手响应里给的 SPKI DER 导入公钥。 */
    fun importPublicKey(spkiDer: ByteArray): PublicKey =
        KeyFactory.getInstance("RSA").generatePublic(X509EncodedKeySpec(spkiDer))

    /** SPKI 指纹 = SHA256(DER) 的 base64（与 Rust `fingerprint_base64()` 一致）。 */
    fun fingerOfSpki(spkiDer: ByteArray): String =
        Base64.getEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(spkiDer))

    /** 带 12 字节包内字段参与的非对称加密体指纹（取 SHA256 的后 16 字节）。 */
    fun bodyFinger(body: ByteArray, nonce12: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        md.update(body)
        md.update(nonce12)
        return md.digest().copyOfRange(16, 32)
    }

    /**
     * 组一个"第二次握手"（SecretHandshakeRequest）包的载荷：
     *   `RSA_PKCS1v15_加密( protobuf(SecretHandshakeRequest{token,key}) ‖ random16 ‖ finger16 )`
     */
    fun buildSecretHandshakePayload(serverPublicKey: PublicKey, token: String, symmetricKey32: ByteArray, nonce12: ByteArray): ByteArray {
        val msg = SecretHandshakeRequest().apply { this.token = token; this.key = symmetricKey32 }.toBytes()
        val random = ByteArray(16).also { rng.nextBytes(it) }

        // body = 报文 ‖ random（RsaSecretBody.body() 的定义）
        val body = msg + random
        val finger = bodyFinger(body, nonce12)
        val plain = body + finger

        val c = Cipher.getInstance("RSA/ECB/PKCS1Padding")
        c.init(Cipher.ENCRYPT_MODE, serverPublicKey)
        return c.doFinal(plain)
    }

    /** 解一个非对称加密体（自检用：本机加密后再用本机私钥解开，验证布局正确）。 */
    fun tryOpenSecretBody(priv: PrivateKey, cipherText: ByteArray, nonce12: ByteArray): ByteArray? {
        return try {
            val c = Cipher.getInstance("RSA/ECB/PKCS1Padding")
            c.init(Cipher.DECRYPT_MODE, priv)
            val plain = c.doFinal(cipherText)
            if (plain.size < 32) return null
            val body = plain.copyOfRange(0, plain.size - 16)
            val finger = plain.copyOfRange(plain.size - 16, plain.size)
            val want = bodyFinger(body, nonce12)
            if (!want.contentEquals(finger)) return null
            body.copyOfRange(0, body.size - 16)
        } catch (_: Exception) { null }
    }
}
