package com.broadcast.vntlite

import java.math.BigInteger
import java.security.SecureRandom

/**
 * X25519（RFC 7748）纯 Kotlin 实现 —— 不依赖系统 JCE。
 *
 * **为什么必须自己实现**：Android 上 `KeyPairGenerator.getInstance("XDH")` /
 * `NamedParameterSpec("X25519")` / `XECPublicKeySpec` 是 **API 33（Android 13）** 才有的；
 * 老机型、以及部分 ROM 上 `KeyPairGenerator.initialize(NamedParameterSpec)` 会抛
 * `No AlgorithmParameterSpec classes are supported` —— 于是配对在"生成本机临时密钥对"
 * 这一步就失败，HELLO 根本发不出去，表现为"配对不上"（真机实测就是这个）。
 *
 * 这里在 GF(2^255-19) 上做 RFC 7748 §5 的 Montgomery 阶梯，Android 8 起都能跑；
 * 结果与电脑端 BouncyCastle 的 X25519 完全一致（两边都按 RFC 7748，
 * 并且两侧都用 RFC 7748 的官方测试向量自检，见 `verify` 工程与 `--self-test`）。
 *
 * 性能：一次标量乘约 1~3 毫秒（BigInteger 实现），配对时只用一两次，完全够。
 */
object X25519 {
    private val P: BigInteger = BigInteger.TWO.pow(255).subtract(BigInteger.valueOf(19))
    /** a24 = (486662 - 2) / 4，RFC 7748 §5 里的常数。 */
    private val A24: BigInteger = BigInteger.valueOf(121665)

    /** RFC 7748 §5 的 decodeScalar25519：清最低 3 位、清最高位、置次高位。 */
    fun clamp(k: ByteArray): ByteArray {
        val out = if (k.size == 32) k.copyOf() else {
            // 容忍长度不为 32 的输入（取低 32 字节，右侧补零）
            val t = ByteArray(32)
            val n = minOf(32, k.size)
            System.arraycopy(k, k.size - n, t, 0, n)
            t
        }
        out[0] = (out[0].toInt() and 248).toByte()
        out[31] = ((out[31].toInt() and 127) or 64).toByte()
        return out
    }

    /** 小端字节串 → 整数。 */
    private fun decodeLittleEndian(b: ByteArray): BigInteger {
        val be = ByteArray(b.size)
        for (i in b.indices) be[i] = b[b.size - 1 - i]
        return BigInteger(1, be)
    }

    /** 整数 → 32 字节小端。 */
    fun encodeLittleEndian(v: BigInteger, len: Int = 32): ByteArray {
        val be = v.mod(P).toByteArray()          // 大端，可能带一个前导 0
        val out = ByteArray(len)
        var i = be.size - 1
        var j = 0
        while (i >= 0 && j < len) { out[j] = be[i]; i--; j++ }
        return out
    }

    /**
     * 标量乘：`scalarMult(k, u)` = RFC 7748 的 X25519(k, u)，返回 32 字节 u 坐标。
     * `k` 会先按 §5 做 clamp；`u` 的最高位按规范忽略。
     */
    fun scalarMult(scalar: ByteArray, u: ByteArray): ByteArray {
        val k = clamp(scalar)
        val uBytes = ByteArray(32)
        val n = minOf(32, u.size)
        System.arraycopy(u, 0, uBytes, 0, n)
        uBytes[31] = (uBytes[31].toInt() and 127).toByte()
        val x1 = decodeLittleEndian(uBytes).mod(P)

        var x2 = BigInteger.ONE
        var z2 = BigInteger.ZERO
        var x3 = x1
        var z3 = BigInteger.ONE
        var swap = 0

        for (t in 254 downTo 0) {
            val kt = (k[t ushr 3].toInt() ushr (t and 7)) and 1
            swap = swap xor kt
            if (swap == 1) {
                var tmp = x2; x2 = x3; x3 = tmp
                tmp = z2; z2 = z3; z3 = tmp
            }
            swap = kt

            val a = x2.add(z2).mod(P)
            val aa = a.multiply(a).mod(P)
            val b = x2.subtract(z2).mod(P)
            val bb = b.multiply(b).mod(P)
            val e = aa.subtract(bb).mod(P)
            val c = x3.add(z3).mod(P)
            val d = x3.subtract(z3).mod(P)
            val da = d.multiply(a).mod(P)
            val cb = c.multiply(b).mod(P)
            x3 = da.add(cb).mod(P).pow(2).mod(P)
            z3 = x1.multiply(da.subtract(cb).mod(P).pow(2).mod(P)).mod(P)
            x2 = aa.multiply(bb).mod(P)
            z2 = e.multiply(aa.add(A24.multiply(e)).mod(P)).mod(P)
        }
        if (swap == 1) {
            var tmp = x2; x2 = x3; x3 = tmp
            tmp = z2; z2 = z3; z3 = tmp
        }
        // 返回 x2 * z2^(p-2)（即 z2 的模逆，等价于 z2^(p-2)）
        // 【不能用 modInverse】z2 ≡ 0 时 modInverse 会抛 ArithmeticException: BigInteger not invertible
        //（真机踩到：配对时手机直接抛异常 → 配不上）。RFC 7748 明确要求这种情况返回 0（全零共享密钥），
        // 所以用 z^(p-2) mod p 求逆 —— z=0 时结果就是 0，不抛异常。
        val res = if (z2.signum() == 0) BigInteger.ZERO else x2.multiply(z2.modPow(P.subtract(BigInteger.TWO), P)).mod(P)
        return encodeLittleEndian(res)
    }

    /** 基点乘：u = 9，得到公钥。 */
    fun basePoint(scalar: ByteArray): ByteArray {
        val nine = ByteArray(32)
        nine[0] = 9
        return scalarMult(scalar, nine)
    }

    /** 随机私钥（已 clamp，可直接当标量用）。 */
    fun newPrivateKey(rng: SecureRandom = SecureRandom()): ByteArray {
        val k = ByteArray(32)
        rng.nextBytes(k)
        return clamp(k)
    }

    /** 十六进制字符串 → 字节（自检用）。 */
    fun hex(s: String): ByteArray {
        val t = s.replace(" ", "").replace("\n", "")
        val out = ByteArray(t.length / 2)
        for (i in out.indices) out[i] = ((Character.digit(t[i * 2], 16) shl 4) or Character.digit(t[i * 2 + 1], 16)).toByte()
        return out
    }

    /** 字节 → 十六进制（自检/日志用）。 */
    fun toHex(b: ByteArray): String {
        val sb = StringBuilder(b.size * 2)
        for (x in b) sb.append(String.format("%02x", x.toInt() and 0xFF))
        return sb.toString()
    }
}
