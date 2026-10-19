package com.broadcast.app

/**
 * **自适应超时**（按 TCP 的 Jacobson/Karels RTO 算法）。
 *
 * 为什么需要：打洞组网的特点是"刚开始慢、越用越快"（首包要握手/打洞，之后走直连）。
 * 固定超时怎么定都不对 —— 定小了刚开始必然误判超时（然后重传、更慢）；定大了网络恢复后
 * 又要白等好几秒。正确做法是**用实测 RTT 自己算**：
 *
 *     SRTT   = (1-1/8)·SRTT + (1/8)·RTT            平滑往返
 *     RTTVAR = (1-1/4)·RTTVAR + (1/4)·|SRTT-RTT|   抖动
 *     超时    = SRTT + 4·RTTVAR                     夹在 [MIN, MAX]
 *
 * 外加两条经验规则（Karn）：**超时就退避翻倍**（网络刚劣化时多给点时间），
 * **成功就按新样本收敛回来**。起步直接取上限（宁可等），之后自动变快 —— 与"打洞越用越快"天然吻合。
 *
 * 界面上不用配置：日志里会打印当前值（如"自适应超时 3.2s → 1.1s"），便于观察。
 */
object AdaptiveTimeout {

    private const val ALPHA = 1.0 / 8.0      // SRTT 的平滑系数
    private const val BETA = 1.0 / 4.0       // RTTVAR 的平滑系数
    private const val MIN_MS = 800.0         // 下限：再快也别低于 0.8s（避免抖动误判）
    private const val MAX_MS = 12000.0       // 上限：再差也别超过 12s
    private const val START_MS = 5000.0      // 起步值：打洞初期偏保守（介于上下限之间）

    @Volatile private var srtt = -1.0        // 平滑 RTT（-1 = 还没有样本）
    @Volatile private var rttvar = 0.0       // 抖动
    @Volatile private var currentMs = START_MS
    @Volatile private var lastLoggedMs = START_MS

    /** 当前超时（毫秒），调用方直接拿去用。 */
    fun current(): Int = currentMs.toInt()

    /** 一次成功往返：把实测 RTT 喂进来（毫秒）。 */
    fun onSample(rttMs: Double) {
        if (rttMs <= 0) return
        if (srtt < 0) {
            srtt = rttMs
            rttvar = rttMs / 2.0
        } else {
            val err = kotlin.math.abs(srtt - rttMs)
            rttvar = (1 - BETA) * rttvar + BETA * err
            srtt = (1 - ALPHA) * srtt + ALPHA * rttMs
        }
        currentMs = (srtt + 4 * rttvar).coerceIn(MIN_MS, MAX_MS)
    }

    /** 超时/失败（Karn）：退避翻倍，并让 RTT 估计向上修正，避免立刻又超时。 */
    fun onTimeout(): Int {
        val doubled = (currentMs * 2).coerceAtMost(MAX_MS)
        currentMs = doubled
        if (srtt > 0) srtt = (srtt * 1.5).coerceAtMost(MAX_MS)
        return currentMs.toInt()
    }

    /** 需要打印时用（变化超过 25% 才值得记一条，避免刷屏）。 */
    fun takeChangeForLog(): String? {
        val now = currentMs
        if (kotlin.math.abs(now - lastLoggedMs) < lastLoggedMs * 0.25) return null
        val s = "自适应超时 ${"%.1f".format(lastLoggedMs / 1000)}s → ${"%.1f".format(now / 1000)}s" +
                (if (srtt > 0) "（实测 RTT 平滑 ${"%.0f".format(srtt)}ms、抖动 ${"%.0f".format(rttvar)}ms）" else "")
        lastLoggedMs = now
        return s
    }

    /** 状态摘要（诊断用）。 */
    val summary: String
        get() = if (srtt < 0) "自适应超时 ${"%.1f".format(currentMs / 1000)}s（还没样本）"
                else "自适应超时 ${"%.1f".format(currentMs / 1000)}s（RTT 平滑 ${"%.0f".format(srtt)}ms / 抖动 ${"%.0f".format(rttvar)}ms）"
}
