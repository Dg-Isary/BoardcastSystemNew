package com.broadcast.app.ui

import androidx.compose.ui.draw.shadow

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import kotlinx.coroutines.delay
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import top.yukonga.miuix.kmp.basic.Card as MiuixCard
import top.yukonga.miuix.kmp.basic.SmallTopAppBar as MiuixSmallTopAppBar

/** 五个页签（用户要求：播控/排程/调休都放底部 Dock）。 */
enum class AppTab(val label: String, val icon: String) {
    Overview("总览", "gauge-high"),
    Player("播控", "circle-play"),
    Tasks("排程", "list-check"),
    Holiday("调休", "calendar-check"),
    Settings("设置", "gear")
}

/**
 * 应用主壳（参照「人脸识别演示程序」的 UI 风格）：
 *   MIUIX Scaffold + SmallTopAppBar + 液态玻璃 GlassDock + 5 页。
 *
 * 全面屏适配（目标 SDK 36：Android 15+ 强制 edge-to-edge）：
 *   顶栏 + 状态栏那一条用**同一个 surface 颜色**铺满，底下压 1px 分隔线；
 *   dock 玻璃条从导航行一直铺到屏幕底边，手势条区域透明占位。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BroadcastAppShell(state: AppState) {
    val snackbar = remember { SnackbarHostState() }
    val density = LocalDensity.current
    var dockSpace by remember { mutableStateOf(84.dp) }
    var barSpace by remember { mutableStateOf(0.dp) }
    val tab = AppTab.entries[state.tabIndex.coerceIn(0, AppTab.entries.size - 1)]

    // 扫码覆盖层（全屏）
    if (state.showScan) {
        // 【必须接管系统返回键】扫码页是覆盖层、不是独立页面：以前按返回会**直接把 App 退出**
        //（用户反馈"二维码处系统返回变退出程序了"）。这里让返回 = 关掉扫码页。
        androidx.activity.compose.BackHandler(enabled = true) { state.showScan = false }
        ScanQrScreen(
            onScanned = { cfg, raw ->
                state.applyScanResult(cfg, raw)
                state.showScan = false
            },
            onCancel = { state.showScan = false },
            galleryPicker = state.galleryQrPicker     // 相册扫码（图标按钮，由 MainActivity 注入系统选图）
        )
        return
    }

    // 话筒配对对话框（唯一需要 6 位动态码的地方；入口只在顶栏「＋」里）
    if (state.showPairDialog) PairDialog(state)

    MiuixScaffold(
        topBar = {
            // 顶栏 + 状态栏那一条用同一个 surface 颜色铺满，底下再压一条 1px 分隔线
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surface)
            ) {
                MiuixSmallTopAppBar(
                    title = when (tab) {
                        AppTab.Overview -> "运行总览"
                        AppTab.Player -> "播控"
                        AppTab.Tasks -> "排程"
                        AppTab.Holiday -> "调休"
                        AppTab.Settings -> "设置"
                    },
                    color = MaterialTheme.colorScheme.surface,
                    actions = {
                        if (state.pairing) {
                            Text(
                                "配对中…",
                                fontSize = 12.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                modifier = Modifier.padding(end = 6.dp)
                            )
                        }
                        // 顶栏动作统一用 FontAwesome 图标（不放文字）：重载 + 「＋」（扫码/配对都收在里面）
                        // 顶栏的「刷新」按钮已去掉（用户：不美观）——各界面靠自动刷新（状态 2 秒一次）
                        // 上下各留 10dp → 顶栏内垂直居中；右侧同样留 10dp → **右边缘距 = 下边距**（用户要求）
                        Box(Modifier.padding(top = 10.dp, bottom = 10.dp, end = 10.dp)) {
                            Box(
                                Modifier
                                    .size(36.dp)
                                    .clip(RoundedCornerShape(18.dp))
                                    .clickableNoRipple { state.showScan = true },
                                contentAlignment = Alignment.Center
                            ) { FaIcon("qrcode", size = 20.dp, tint = MaterialTheme.colorScheme.primary) }
                            // 右上角「＋」：改成**底部滑出面板**（与任务/调休编辑、App 整体风格统一），
                            // 不再用 Material 的 DropdownMenu（那个小气泡跟玻璃卡片风格不搭）
                            if (false) {   // 「＋」菜单已废弃：改成图标的扫码入口，见上面那行
                                AppDialog(onDismissRequest = { state.showQuickMenu = false }) {
                                    Text("添加 / 配置", fontSize = 17.sp)
                                    Spacer(Modifier.height(6.dp))
                                    SheetRow("qrcode", "扫码配置", "扫电脑端「运行总览」右侧的二维码") {
                                        state.showQuickMenu = false; state.showScan = true
                                    }
                                    ThinDivider()
                                    SheetRow(
                                        "key",
                                        if (state.paired) "话筒状态（已配对）" else "话筒配对（输动态码）",
                                        "输 6 位动态码，或已信任则免码直连"
                                    ) { state.showQuickMenu = false; state.showPairDialog = true }
                                }
                            }
                        }
                    }
                )
                HorizontalDivider(
                    thickness = 1.dp,
                    color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.55f)
                )
            }
        },
        snackbarHost = { SnackbarHost(snackbar) },
        containerColor = MaterialTheme.colorScheme.background,
        contentWindowInsets = WindowInsets(0, 0, 0, 0)
    ) { padding ->
        Box(Modifier.fillMaxSize()) {
            // 内容区：让开顶栏 + 底部（dock + 贴手动作条）
            Box(
                Modifier
                    .fillMaxSize()
                    .padding(top = padding.calculateTopPadding())
                    .padding(bottom = dockSpace + barSpace + 12.dp)
            ) {
                when (tab) {
                    AppTab.Overview -> OverviewScreen(state)
                    AppTab.Player -> PlayerScreen(state)
                    AppTab.Tasks -> TasksScreen(state)
                    AppTab.Holiday -> HolidayScreen(state)
                    AppTab.Settings -> SettingsScreen(state)
                }
            }

            // 贴手动作条（用户要求：按钮放离手更近）——只在这两个页签存在。
            // 【坑】高度只能算"条本身"：以前把 onSizeChanged 挂在**带 dock 占位 padding 的 Column** 上，
            // 量到的高度里含了 dock 那一段，于是内容区又多让出一块空白 —— 用户看到的就是 dock 上方那条白色区域。
            if (tab == AppTab.Overview || tab == AppTab.Player) {
                Column(
                    Modifier
                        .align(Alignment.BottomCenter)
                        .fillMaxWidth()
                        .padding(bottom = dockSpace + 6.dp, start = 14.dp, end = 14.dp)
                ) {
                    Box(Modifier.onSizeChanged { barSpace = with(density) { it.height.toDp() } }) {
                        if (tab == AppTab.Overview) ThumbMicBar(state) else ThumbPlayerBar(state)
                    }
                }
            } else {
                // 没有动作条的页签：占位归零（否则会残留上一页的高度，底部留一条空白）
                LaunchedEffect(tab) { barSpace = 0.dp }
            }

            // 底部悬浮导航栏（液态玻璃）—— 挂在最外层、每个页面同一位置
            GlassDock(
                page = tab.ordinal,
                onSelect = { state.tabIndex = it },
                onMeasured = { h -> dockSpace = with(density) { h.toDp() } },
                modifier = Modifier.align(Alignment.BottomCenter)
            )
        }
    }
}

/**
 * 贴手动作条 · 总览：一个「送话 / 静音」复合大按钮（图标 + 整块颜色）。
 * 放在 dock 正上方，拇指自然落点；没配对时点它直接弹配对框。
 */
@Composable
private fun ThumbMicBar(state: AppState) {
    val live = state.micLive
    val bg = if (live) Color(0xFF2AA45C) else MaterialTheme.colorScheme.surfaceVariant
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(bg)
            .clickableNoRipple { state.toggleMic() }
            .padding(vertical = 15.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically
    ) {
        FaIcon(
            if (live) "microphone" else "microphone-slash",
            size = 21.dp,
            tint = if (live) Color.White else MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.width(10.dp))
        Text(
            when {
                !state.paired -> "配对后送话"
                live -> "送话中（点一下静音）"
                else -> "已静音（点一下送话）"
            },
            fontSize = 15.sp,
            fontWeight = FontWeight.Medium,
            color = if (live) Color.White else MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/** 贴手动作条 · 播控：可拖动进度条 + 上一首/播放暂停/下一首。 */
@Composable
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
private fun ThumbPlayerBar(state: AppState) {
    var drag by remember { mutableStateOf<Float?>(null) }
    val mp = state.sysJson?.optJSONObject("manual_player")
    val dur = state.playerDuration
    val playing = mp?.optString("status") == "playing"
    // 【乐观更新】(A) 播放/暂停 与 播放模式：点下去**立刻变图标**，等电脑端真实状态回来再对齐
    //（用户要求：先变图标再同步状态，否则不够跟手）
    var pendPlaying by remember { mutableStateOf<Boolean?>(null) }
    var pendModeIcon by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(playing) { if (pendPlaying != null && pendPlaying == playing) pendPlaying = null }
    LaunchedEffect(mp?.optString("mode")) { pendModeIcon = null }
    LaunchedEffect(pendPlaying) { if (pendPlaying != null) { kotlinx.coroutines.delay(3000); pendPlaying = null } }
    val playingNow = pendPlaying ?: playing
    val modeNowIcon = pendModeIcon ?: modeIcon(mp?.optString("mode"))
    // 当前歌名（取自电脑端队列的 current_index）
    val queueNow = mp?.optJSONArray("queue")
    val idxNow = mp?.optInt("current_index", -1) ?: -1
    val songName = if (queueNow != null && idxNow in 0 until queueNow.length())
        queueNow.optJSONObject(idxNow)?.optString("name").orEmpty() else ""
    // 每 400ms 重算一次显示位置：拖动后它**自己往前走**（与听到的一致），没拖动时也在两次轮询之间平滑插值
    var tick by remember { mutableStateOf(0L) }
    LaunchedEffect(Unit) {
        while (true) {
            delay(400)
            tick = System.currentTimeMillis()
        }
    }
    val posSec = remember(tick, state.pinnedSeek, state.sysJson) {
        state.displayPositionSeconds(playing, state.lastDataAt)
    }
    // 优先级：正在拖 > 上面算出来的显示位置
    val frac = drag ?: (if (dur > 0) (posSec / dur).toFloat().coerceIn(0f, 1f) else 0f)
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.92f))
            .padding(horizontal = 12.dp, vertical = 8.dp)
    ) {
        DragBar(
            fraction = frac,
            enabled = state.dataReady && dur > 0,
            onDrag = { drag = it },
            onFinished = { f ->
                drag = null
                if (dur > 0) state.seekTo((f * dur).toDouble())
            }
        )
        // 开始时间 ——（歌名）—— 结束时间（用户要求：歌名放这两个时间中间；限宽，过长横向滚动）
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(fmtClock(if (drag != null && dur > 0) drag!! * dur else posSec),
                fontSize = 10.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (drag != null || state.pinnedSeek != null) {
                Spacer(Modifier.width(6.dp))
                Text("定位中…", fontSize = 10.sp, color = MaterialTheme.colorScheme.primary)
            }
            Box(Modifier.weight(1f).padding(horizontal = 10.dp), contentAlignment = Alignment.Center) {
                MarqueeText(if (songName.isBlank()) "（队列为空）" else songName)
            }
            Text(fmtClock(dur), fontSize = 10.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Spacer(Modifier.height(4.dp))
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceEvenly,
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconBubble("backward-step", state.dataReady) { state.playerCmd("prev") }
            IconBubble("stop", state.dataReady) { state.playerCmd("stop") }
            IconBubble(if (playingNow) "pause" else "play", state.dataReady, primary = true, big = true) {
                pendPlaying = !playingNow                              // 立刻切图标（不等电脑端）
                state.playerCmd(if (playingNow) "pause" else "play")
            }
            IconBubble("forward-step", state.dataReady) { state.playerCmd("next") }
            IconBubble(modeNowIcon, state.dataReady) {
                // 立刻切到"下一档"图标（顺序 → 循环 → 随机），电脑端回包后再对齐
                pendModeIcon = when (modeNowIcon) {
                    "arrow-right" -> "repeat"
                    "repeat" -> "shuffle"
                    else -> "arrow-right"
                }
                state.playerCmd("mode")
            }
        }
    }
}

/** 播放模式图标：**当前状态**（用户要求）；补齐"顺序播放（不循环）"这一档。 */
private fun modeIcon(mode: String?): String = when (mode?.lowercase()) {
    "loop", "list", "all" -> "repeat"          // 列表循环
    "single", "one", "repeat_one" -> "repeat"  // 单曲循环（字形缺失时退化为 repeat）
    "shuffle", "random" -> "shuffle"           // 随机
    else -> "arrow-right"                      // 顺序播放（不循环）—— 以前缺这一档
}

private fun fmtClock(sec: Double): String {
    if (sec <= 0 || sec.isNaN()) return "0:00"
    val s = sec.toInt()
    return "%d:%02d".format(s / 60, s % 60)
}

/** 圆形图标按钮（贴手动作条用）。 */
@Composable
private fun IconBubble(
    icon: String,
    enabled: Boolean,
    primary: Boolean = false,
    big: Boolean = false,
    onClick: () -> Unit
) {
    val size = if (big) 52.dp else 42.dp
    Box(
        Modifier
            .size(size)
            .clip(CircleShape)
            .background(
                when {
                    !enabled -> MaterialTheme.colorScheme.surface.copy(alpha = 0.4f)
                    primary -> MaterialTheme.colorScheme.primary
                    else -> MaterialTheme.colorScheme.surface
                }
            )
            .then(if (enabled) Modifier.clickableNoRipple(onClick) else Modifier),
        contentAlignment = Alignment.Center
    ) {
        FaIcon(
            icon,
            size = if (big) 21.dp else 17.dp,
            tint = when {
                !enabled -> MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
                primary -> MaterialTheme.colorScheme.onPrimary
                else -> MaterialTheme.colorScheme.onSurface
            }
        )
    }
}

/**
 * 话筒配对对话框（顶栏「＋」→ 话筒配对）。
 * 只有喊话需要这 6 位动态码：配对成功后拿到 sid + 会话密钥做音频 AEAD；
 * 播控/排程/调休走数据通道，不需要它。
 */
@Composable
private fun PairDialog(state: AppState) {
    var code by remember { mutableStateOf("") }
    AppDialog(
        onDismissRequest = { state.showPairDialog = false },
        title = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                FaIcon("key", size = 16.dp, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(8.dp))
                Text(if (state.paired) "话筒会话" else "话筒配对")
            }
        },
        text = {
            Column {
                Text(
                    if (state.paired)
                        "已配对：sid=${state.pairingSid}。喊话走这个会话；要重新配对就先在电脑端「话筒开关」窗点一次开麦。"
                    else
                        "在电脑端「话筒开关」窗点『开麦』，把窗里显示的 6 位数字动态码填到这里（60 秒轮换、用一次作废）。",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                if (!state.paired) {
                    Spacer(Modifier.height(10.dp))
                    AppField(
                        value = code,
                        onValueChange = { v -> code = v.filter { it.isDigit() }.take(6) },
                        label = { Text("6 位数字动态码") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                        modifier = Modifier.fillMaxWidth()
                    )
                }
            }
        },
        confirmButton = {
            if (state.paired) {
                TextButton(onClick = { state.showPairDialog = false }) { Text("知道了") }
            } else {
                TextButton(
                    enabled = state.connected && !state.pairing && code.length == 6,
                    onClick = { state.pairWithCode(code); state.showPairDialog = false }
                ) { Text(if (state.pairing) "配对中…" else "配对") }
            }
        },
        dismissButton = { TextButton(onClick = { state.showPairDialog = false }) { Text("取消") } }
    )
}

/** MIUIX 风格的 Scaffold（包一层，避免各处重复写 containerColor/contentWindowInsets）。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MiuixScaffold(
    topBar: @Composable () -> Unit,
    snackbarHost: @Composable () -> Unit,
    containerColor: Color,
    contentWindowInsets: WindowInsets,
    content: @Composable (androidx.compose.foundation.layout.PaddingValues) -> Unit
) {
    Scaffold(
        topBar = topBar,
        snackbarHost = snackbarHost,
        containerColor = containerColor,
        contentWindowInsets = contentWindowInsets,
        content = content
    )
}

/** 液态玻璃底部导航栏（参照「人脸识别演示程序」的 GlassDock）。 */
@Composable
private fun GlassDock(
    page: Int,
    onSelect: (Int) -> Unit,
    onMeasured: (Int) -> Unit,
    modifier: Modifier = Modifier
) {
    val dark = androidx.compose.foundation.isSystemInDarkTheme()
    val barShape = RoundedCornerShape(26.dp)
    val sideMargin = 14.dp
    val gestureInset = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
    val accent = MaterialTheme.colorScheme.primary

    // 首次出现：整体从下方滑上来 + 淡入
    val appear = remember { Animatable(0f) }
    LaunchedEffect(Unit) {
        appear.animateTo(1f, spring(dampingRatio = 0.75f, stiffness = Spring.StiffnessLow))
    }

    Column(
        modifier
            .fillMaxWidth()
            .onSizeChanged { onMeasured(it.height) }
            .graphicsLayer {
                translationY = (1f - appear.value) * 56.dp.toPx()
                alpha = appear.value
            }
    ) {
        BoxWithConstraints(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = sideMargin)
        ) {
            val itemWidth = maxWidth / 5
            val barWidth = maxWidth
            // ① 指示器位置：spring 滑过去
            val targetX = itemWidth * page
            val indicatorX by animateDpAsState(
                targetValue = targetX,
                animationSpec = spring(dampingRatio = 0.60f, stiffness = Spring.StiffnessMediumLow),
                label = "dockIndicator"
            )
            // ② 液态"拉开再收"：换页时指示器先明显拉长，再弹回原宽
            val stretch = remember { Animatable(1f) }
            LaunchedEffect(page) {
                stretch.snapTo(1f)
                stretch.animateTo(1.5f, tween(110))
                stretch.animateTo(1f, spring(dampingRatio = 0.38f, stiffness = 300f))
            }
            val pillWidth = itemWidth * stretch.value
            val pillX = indicatorX + (itemWidth - pillWidth) / 2

            // ③ 玻璃光泽缓慢扫过（无限循环，让玻璃看起来"活着"）
            val sweep = rememberInfiniteTransition(label = "glassSweep")
            val sweepPhase by sweep.animateFloat(
                initialValue = -0.5f,
                targetValue = 1.5f,
                animationSpec = infiniteRepeatable(
                    animation = tween(durationMillis = 5200, easing = LinearEasing),
                    repeatMode = RepeatMode.Restart
                ),
                label = "sweepPhase"
            )

            Box(
                Modifier
                    .fillMaxWidth()
                    // 【亮色主题看不见玻璃的修复】浅色页面上"白色半透明叠白底"等于没有效果。
                    // 现在：①底色改成带冷调的灰蓝半透明（不再是纯白）；②描边改成"上亮下暗"的双色边。
                    // 注意：这里**不能用 Modifier.shadow** —— Compose 的阴影会按当前表面色渲染，
                    // 亮色主题下就画出"白色长方形阴影"（用户反馈的正是这个），所以改用描边来表达"浮起来"。
                    .clip(barShape)
                    .background(
                        Brush.verticalGradient(
                            if (dark) {
                                listOf(Color(0xB30B0D11), Color(0x8C14171C))
                            } else {
                                listOf(Color(0xE8F5F8FC), Color(0xC7E4EAF2))
                            }
                        )
                    )
                    .border(
                        BorderStroke(
                            1.dp,
                            Brush.verticalGradient(
                                if (dark) {
                                    listOf(Color(0x4DFFFFFF), Color(0x0DFFFFFF))
                                } else {
                                    listOf(Color(0xFFFFFFFF), Color(0x330B1B2A))
                                }
                            )
                        ),
                        barShape
                    )
            ) {
                Box(
                    Modifier
                        .fillMaxWidth()
                        .padding(vertical = 6.dp)
                ) {
                    // ① 玻璃光泽：一条很淡的高光缓慢扫过
                    Box(Modifier.matchParentSize()) {
                        Box(
                            Modifier
                                .offset(x = barWidth * sweepPhase)
                                .width(barWidth * 0.45f)
                                .fillMaxHeight()
                                .background(
                                    Brush.horizontalGradient(
                                        listOf(
                                            Color.Transparent,
                                            Color.White.copy(alpha = if (dark) 0.10f else 0.26f),
                                            Color.Transparent
                                        )
                                    )
                                )
                        )
                    }
                    // ② 滑动指示器：实心主题色
                    Box(Modifier.matchParentSize()) {
                        Box(
                            Modifier
                                .offset(x = pillX)
                                .width(pillWidth)
                                .fillMaxHeight()
                                .padding(horizontal = 7.dp, vertical = 2.dp)
                                .clip(RoundedCornerShape(20.dp))
                                .background(
                                    Brush.verticalGradient(
                                        listOf(
                                            accent.copy(alpha = 0.98f),
                                            accent.copy(alpha = 0.84f)
                                        )
                                    )
                                )
                                .border(
                                    1.dp,
                                    Color.White.copy(alpha = if (dark) 0.28f else 0.45f),
                                    RoundedCornerShape(20.dp)
                                )
                        )
                    }
                    // ③ 图标行（高度自适应）
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 3.dp),
                        horizontalArrangement = Arrangement.SpaceEvenly,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        AppTab.entries.forEachIndexed { i, t ->
                            DockItem(
                                tab = t,
                                selected = i == page,
                                onClick = { onSelect(i) },
                                modifier = Modifier.weight(1f)
                            )
                        }
                    }
                }
            }
        }
        // 手势条区域：透明占位（不画任何东西，露出页面底色/相机画面）
        Spacer(Modifier.height(gestureInset))
    }
}

/** Dock 里的单个图标项（图标 + 文字，高度自适应）。 */
@Composable
private fun DockItem(
    tab: AppTab,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    val scheme = MaterialTheme.colorScheme
    val tint = if (selected) scheme.onPrimary else scheme.onSurfaceVariant
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = modifier
            .clip(RoundedCornerShape(16.dp))
            .clickableNoRipple(onClick = onClick)
            .padding(horizontal = 4.dp, vertical = 2.dp)
    ) {
        FaIcon(tab.icon, size = 20.dp, tint = tint)
        Spacer(Modifier.height(2.dp))
        Text(tab.label, fontSize = 10.sp, color = tint, maxLines = 1)
    }
}

/** 底部面板里的一行（图标 + 标题 + 说明），点击即执行；用于右上角「＋」菜单。 */
@Composable
fun SheetRow(icon: String, title: String, subtitle: String, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickableNoRipple(onClick)
            .padding(vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier
                .size(38.dp)
                .clip(RoundedCornerShape(19.dp))
                .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.12f)),
            contentAlignment = Alignment.Center
        ) { FaIcon(icon, size = 17.dp, tint = MaterialTheme.colorScheme.primary) }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 14.5.sp)
            if (subtitle.isNotBlank()) {
                Text(subtitle, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        FaIcon("chevron-right", size = 14.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/**
 * **过长就横向滚动**的歌名（用户要求）。自己实现，不依赖 basicMarquee（当前 Compose 版本没有它）。
 * 只有当文字宽度超过可用宽度时才滚动，来回往返，速度按超出长度自适应。
 */
@Composable
private fun MarqueeText(text: String, fontSize: androidx.compose.ui.unit.TextUnit = 11.sp) {
    var boxW by remember { mutableStateOf(0) }
    var textW by remember { mutableStateOf(0) }
    val overflow = (textW - boxW).coerceAtLeast(0)
    val anim = androidx.compose.animation.core.rememberInfiniteTransition(label = "marquee")
    val x by anim.animateFloat(
        initialValue = 0f,
        targetValue = -overflow.toFloat(),
        animationSpec = androidx.compose.animation.core.infiniteRepeatable(
            animation = androidx.compose.animation.core.tween(
                durationMillis = (overflow * 22).coerceIn(1200, 9000),
                easing = androidx.compose.animation.core.LinearEasing
            ),
            repeatMode = androidx.compose.animation.core.RepeatMode.Reverse
        ),
        label = "marquee-x"
    )
    Box(Modifier.fillMaxWidth().onSizeChanged { boxW = it.width }) {
        androidx.compose.material3.Text(
            text,
            fontSize = fontSize,
            color = MaterialTheme.colorScheme.onSurface,
            maxLines = 1,
            softWrap = false,
            modifier = Modifier
                .offset { androidx.compose.ui.unit.IntOffset(if (overflow > 0) x.toInt() else 0, 0) }
                .onSizeChanged { textW = it.width }
        )
    }
}
