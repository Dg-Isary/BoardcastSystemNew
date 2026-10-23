package com.broadcast.app.ui

import androidx.compose.runtime.LaunchedEffect
import kotlinx.coroutines.delay

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONArray
import org.json.JSONObject
import java.util.Calendar

/**
 * 五个页面。
 *
 * 这一版按用户要求重排：
 *   · **总览**只留"组网状态 + 话筒状态"两行（图标/颜色/文字），点一下跳到对应设置；外加一个「送话 / 静音」复合按钮；
 *   · 扫码与话筒配对**只**在顶栏「＋」菜单里（见 App.kt），其它页面不再出现；
 *   · **播控**：全 FontAwesome 图标 + 可拖动进度条（电脑端 cmd=seek）；
 *   · **排程 / 调休**：带编辑（走 cbs-api 数据通道，不需要配对码）；
 *   · **设置**：外观主题色（色块，不写字）、完整连接配置 + 手动配置、日志导出；话筒设置不再放这里。
 */

@Composable
private fun PageColumn(content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 12.dp, vertical = 8.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
        content = content
    )
}

/** 数据通道没就绪时的统一提示（扫码入口在顶栏「＋」里，这里只说明该干什么）。 */
@Composable
private fun NeedData(state: AppState) {
    AppCard {
        SectionTitle(
            when {
                !state.configured -> "还没有连接广播主机"
                !state.registered -> "正在自动组网…"
                else -> "还差一个数据密码"
            }
        )
        Spacer(Modifier.height(6.dp))
        Text(
            when {
                !state.configured ->
                    "点右上角「＋」→『扫码配置』，对着电脑端「vnt 设置」窗里的二维码扫一下。" +
                        "服务器、令牌、数据密码、对端地址会一次填好，并且记住这次配置，以后打开就自动组网。"
                !state.registered ->
                    "已用上次保存的配置自动组网" +
                        (if (state.reconnectCount > 0) "（已重试 ${state.reconnectCount} 次）" else "") +
                        "。连不上会一直自动重试，不用管；要换主机再扫码。"
                else ->
                    "电脑端「vnt 设置」里没填数据密码，所以手机连不上它的数据通道。请在电脑端填一个数据密码，" +
                        "再点右上角「＋」→『扫码配置』重新扫一次。"
            },
            fontSize = 12.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        if (state.statusLine.isNotBlank()) {
            Spacer(Modifier.height(6.dp))
            Text(state.statusLine, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

// ===================== 总览（精简） =====================

/** 状态行：FontAwesome 图标 + 颜色点 + 文字；整行可点（跳到对应设置页）。 */
@Composable
private fun StatusLine(
    icon: String,
    title: String,
    detail: String,
    color: Color,
    onClick: (() -> Unit)? = null
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .then(if (onClick != null) Modifier.clickableNoRipple(onClick) else Modifier)
            .padding(horizontal = 4.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier
                .size(34.dp)
                .clip(CircleShape)
                .background(color.copy(alpha = 0.16f)),
            contentAlignment = Alignment.Center
        ) { FaIcon(icon, size = 17.dp, tint = color) }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 14.sp, fontWeight = FontWeight.Medium, color = MaterialTheme.colorScheme.onSurface)
            if (detail.isNotBlank()) {
                Text(detail, fontSize = 11.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
        if (onClick != null) {
            FaIcon("chevron-down", size = 13.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
fun OverviewScreen(state: AppState) {
    PageColumn {
        if (!state.configured) NeedData(state)

        AppCard {
            SectionTitle("状态")
            Spacer(Modifier.height(2.dp))
            // 组网状态：绿=已入网，琥珀=组网中，红=未配置
            val netColor = when {
                state.registered -> Color(0xFF2AA45C)
                state.configured -> Color(0xFFF08A00)
                else -> MaterialTheme.colorScheme.error
            }
            StatusLine(
                icon = "tower-broadcast",
                title = "组网",
                detail = when {
                    state.registered -> "已入网 · 我的地址 ${state.myVirtualIp.ifBlank { "分配中" }}"
                    state.configured -> "组网中…（已重试 ${state.reconnectCount} 次，自动重连）"
                    else -> "未配置：点右上角「＋」扫码"
                },
                color = netColor,
                onClick = { state.tabIndex = 4 }
            )
            ThinDivider()
            // 话筒状态：绿=送话中，灰=静音/未配对
            val micColor = when {
                state.micLive -> Color(0xFF2AA45C)
                state.paired -> Color(0xFF8A8F98)
                else -> Color(0xFFF08A00)
            }
            StatusLine(
                icon = if (state.micLive) "microphone" else "microphone-slash",
                title = "话筒",
                detail = state.micStateText,
                color = micColor,
                onClick = { if (!state.paired) state.showPairDialog = true else state.tabIndex = 4 }
            )
            Spacer(Modifier.height(6.dp))
            ThinDivider()
            // 采集源：**麦克风 / 系统声音 两个独立开关**（用户要求），可同时开 = 混音后一起送
            Spacer(Modifier.height(8.dp))
            Text("采集源", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(6.dp))
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                FaIcon("microphone", size = 15.dp,
                    tint = if (state.micSourceOn) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(8.dp))
                // 两行文字都用固定行高 + 垂直居中：以前"麦克风"（单行）和"系统声音（内录）"（两行）
                // 顶对齐，看着就是没对齐（用户反馈）
                Text("麦克风", fontSize = 13.sp, modifier = Modifier.weight(1f), maxLines = 1)
                MiuixSwitchFor(state.micSourceOn) { state.setMicSource(it) }
            }
            Spacer(Modifier.height(6.dp))
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                FaIcon("volume-high", size = 15.dp,
                    tint = if (state.sysSourceOn) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(8.dp))
                Text("系统声音", fontSize = 13.sp, modifier = Modifier.weight(1f), maxLines = 1)
                MiuixSwitchFor(state.sysSourceOn) { state.setSystemSource(it) }
            }
            Spacer(Modifier.height(4.dp))
            Text(
                if (state.sysPending) "等待系统授权…"
                else "内录需要系统弹框授权一次；部分 App 禁止被内录，通话与 DRM 音频采不到",
                fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(10.dp))
            // 送话音量：滑条 + 手机**物理音量键**（正在送话时音量键调的就是它）
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                FaIcon("volume-low", size = 14.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(8.dp))
                Text("送话音量", fontSize = 12.5.sp)
                Spacer(Modifier.weight(1f))
                Text("${state.sendGainPercent}%", fontSize = 12.sp, color = MaterialTheme.colorScheme.primary)
            }
            VolumeBar(
                percent = state.sendGainPercent,
                onChange = { state.applySendGain(it) },
                modifier = Modifier.fillMaxWidth()
            )
            Text(
                "正在送话时，手机侧边的音量键直接调这一项",
                fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(6.dp))
            Text("当前：" + state.audioSourceText, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (state.lastError.isNotBlank()) {
                Spacer(Modifier.height(8.dp))
                Text("最近错误：${state.lastError}", fontSize = 11.sp, color = MaterialTheme.colorScheme.error)
            }
        }
    }
}

// ===================== 播控 =====================

@Composable
fun PlayerScreen(state: AppState) {
    PageColumn {
        if (!state.dataReady) NeedData(state)
            if (!state.isAdminDevice) ReadOnlyHint("普通权限：只读（编辑/新增/删除已隐藏，需要管理员请让电脑端赋权）")

        val sys = state.sysJson
        val mp = sys?.optJSONObject("manual_player")
        val idx = mp?.optInt("current_index", 0) ?: 0
        val queue = mp?.optJSONArray("queue")
        val nowName = if (queue != null && idx < queue.length()) queue.optJSONObject(idx)?.optString("name").orEmpty() else ""

        // 播控页顶部的"播控"卡片已删除（用户要求）：歌名改放到贴手条的进度条下方（开始/结束时间之间）

        AppCard {
            SectionTitle("队列" + (if (queue != null) "（${queue.length()} 首）" else ""))
            Spacer(Modifier.height(6.dp))
            if (queue == null || queue.length() == 0) EmptyHint("队列为空：从下面音乐库「入队」")
            else for (i in 0 until minOf(queue.length(), 20)) {
                val o = queue.optJSONObject(i) ?: continue
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp))
                        // 点整行（点歌名）就切到这首；右侧图标按钮仍然可用
                        .clickableNoRipple { state.playerCmd("play_idx", i) }
                        .padding(vertical = 5.dp, horizontal = 4.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    if (i == idx) FaIcon("circle-play", size = 13.dp, tint = MaterialTheme.colorScheme.primary)
                    else Spacer(Modifier.width(13.dp))
                    Spacer(Modifier.width(8.dp))
                    Text(
                        o.optString("name"),
                        fontSize = 12.5.sp,
                        modifier = Modifier.weight(1f),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    IconAction("play", "播这首", state.dataReady, compact = true) { state.playerCmd("play_idx", i) }
                    IconAction("trash", "移出队列", state.dataReady, compact = true) { state.playerCmd("remove", i) }
                }
            }
        }

        AppCard {
            SectionTitle("音乐库（点「入队」加入播控队列）")
            Spacer(Modifier.height(6.dp))
            val names = state.musicNames()
            if (names.isEmpty()) EmptyHint(if (state.dataReady) "没有音乐条目或正在获取…" else "入网后显示")
            else for (i in 0 until minOf(names.size, 20)) {
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    FaIcon("circle-play", size = 12.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.width(8.dp))
                    Text(names[i], fontSize = 12.5.sp, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    IconAction("plus", "入队", state.dataReady, compact = true) { state.queueAdd(i) }
                }
            }
        }
    }
}

/**
 * 自绘开关（麦克风 / 系统声音两个独立开关用）。
 * 不直接用 MIUIX 的 Switch：这里只需要一个"开=主题色、关=灰"的简洁开关，自绘可控且不依赖具体版本 API。
 */
@Composable
private fun MiuixSwitchFor(checked: Boolean, onChange: (Boolean) -> Unit) {
    Box(
        Modifier
            .size(46.dp, 26.dp)
            .clip(RoundedCornerShape(13.dp))
            .background(if (checked) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant)
            .clickableNoRipple { onChange(!checked) },
        contentAlignment = if (checked) Alignment.CenterEnd else Alignment.CenterStart
    ) {
        Box(
            Modifier
                .padding(horizontal = 3.dp)
                .size(20.dp)
                .clip(CircleShape)
                .background(Color.White)
        )
    }
}

/** 一个图标按钮（圆形底 + FontAwesome 字形），播控页大量使用；界面里不放文字按钮。 */
@Composable
private fun IconAction(
    icon: String,
    tip: String,
    enabled: Boolean,
    primary: Boolean = false,
    compact: Boolean = false,
    /** true = 不可用时**整块隐藏**（用户确认：普通设备看不到编辑/新增/删除按钮，而不是置灰）。 */
    hideWhenDisabled: Boolean = false,
    onClick: () -> Unit
) {
    if (hideWhenDisabled && !enabled) return
    val size = if (compact) 30.dp else 46.dp
    // 前景色：品牌色圆钮用偏亮的灰（OnBrandText，与 AppButton/AppOutlinedButton 统一）；
    // 中性圆钮用 onSurface（底色是 surfaceVariant，属于中性面，深字/浅字都成立）。
    val tint = when {
        !enabled -> MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
        primary -> OnBrandText
        else -> MaterialTheme.colorScheme.onSurface
    }
    Box(
        Modifier
            .size(size)
            .clip(CircleShape)
            .background(
                when {
                    !enabled -> MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.35f)
                    primary -> MaterialTheme.colorScheme.primary
                    else -> MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.6f)
                }
            )
            .then(if (enabled) Modifier.clickableNoRipple(onClick) else Modifier),
        contentAlignment = Alignment.Center
    ) {
        FaIcon(icon, size = if (compact) 13.dp else 19.dp, tint = tint)
    }
}

private fun fmtTime(sec: Double): String {
    if (sec <= 0 || sec.isNaN()) return "0:00"
    val s = sec.toInt()
    return "%d:%02d".format(s / 60, s % 60)
}

private fun statusText(s: String) = when (s) {
    "playing" -> "播放中"
    "paused" -> "已暂停"
    else -> "已停止"
}

private fun modeText(m: String) = when (m) {
    "loop" -> "列表循环"
    "single" -> "单曲循环"
    else -> "顺序播放"
}

// ===================== 排程（可编辑） =====================

@Composable
fun TasksScreen(state: AppState) {
    var editing by remember { mutableStateOf<JSONObject?>(null) }
    var creating by remember { mutableStateOf(false) }
    PageColumn {
        if (!state.dataReady) NeedData(state)
            if (!state.isAdminDevice) ReadOnlyHint("普通权限：只读（编辑/新增/删除已隐藏，需要管理员请让电脑端赋权）")
        AppCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle("任务排程", Modifier.weight(1f))
                IconAction("plus", "新增任务", state.dataReady && state.isAdminDevice, hideWhenDisabled = true) { creating = true }
            }
            Spacer(Modifier.height(6.dp))
            val arr = state.tasksJson
            if (arr == null) EmptyHint("正在获取…")
            else if (arr.length() == 0) EmptyHint("还没有任务：点右上角「＋」新增")
            else {
                val today = todayTasks(arr)
                Text(
                    "今天会触发 ${today.size} 条 / 共 ${arr.length()} 条",
                    fontSize = 11.5.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(6.dp))
                for (i in 0 until arr.length()) {
                    val t = arr.optJSONObject(i) ?: continue
                    val vol = if (t.has("volume")) t.optInt("volume") else 100
                    val dur = t.optInt("duration_m", 0)
                    val durS = t.optInt("duration_s", 0)
                    val ids = t.optJSONArray("music_ids")
                    val days = t.optJSONArray("days")
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 5.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column(Modifier.weight(1f)) {
                            // 【曲名与星期对调】第一行：时间 + 编号 + **曲目名称**（以前这一行是星期）
                            Text(
                                "${t.optString("start_time").take(5)}　#${t.optInt("id")}　${state.musicNamesOf(ids, 2)}",
                                fontSize = 13.sp,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            // 【曲名与星期对调】第二行：**星期** + 音量/时长/播放方式（以前曲名在这一行）
                            Text(
                                "${daysText(days)} · 音量 $vol% · " +
                                    (if (dur > 0) "${dur}分" else "") + (if (durS > 0) "${durS}秒" else "") +
                                    (if (dur == 0 && durS == 0) "整曲" else "") +
                                    " · " + (if (t.optString("sequence_mode") == "random") "随机" else "顺序"),
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                        IconAction("pen-to-square", "编辑", state.dataReady && state.isAdminDevice, compact = true, hideWhenDisabled = true) { editing = t }
                        Spacer(Modifier.width(6.dp))
                        IconAction("trash", "删除", state.dataReady && state.isAdminDevice, compact = true, hideWhenDisabled = true) { state.deleteTask(t.optInt("id")) }
                    }
                    if (i < arr.length() - 1) ThinDivider()
                }
            }
        }
        AppCard {
            SectionTitle("说明")
            Spacer(Modifier.height(4.dp))
            Text(
                "与电脑端、网页同一份 tasks.jsonl；手机端保存时会把原来不认识的字段一起带回去，不会丢配置。" +
                    "新增/修改后电脑端立即生效。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
    if (creating || editing != null) {
        TaskEditDialog(
            state = state,
            task = editing,
            onDismiss = { creating = false; editing = null }
        )
    }
}

/** 星期几 → 文字（电脑端 1..7 = 周一..周日）。 */
private fun daysText(days: JSONArray?): String {
    if (days == null || days.length() == 0) return "不按星期"
    val names = arrayOf("一", "二", "三", "四", "五", "六", "日")
    val on = ArrayList<String>()
    for (i in 0 until days.length()) {
        val d = days.optInt(i)
        if (d in 1..7) on.add(names[d - 1])
    }
    return "周" + on.joinToString("")
}

@Composable
private fun TaskEditDialog(state: AppState, task: JSONObject?, onDismiss: () -> Unit) {
    val isNew = task == null
    var time by remember { mutableStateOf(task?.optString("start_time")?.take(5) ?: "08:00") }
    var volume by remember { mutableStateOf((task?.optInt("volume", 100) ?: 100).toString()) }
    var durationM by remember { mutableStateOf((task?.optInt("duration_m", 0) ?: 0).toString()) }
    var durationS by remember { mutableStateOf((task?.optInt("duration_s", 0) ?: 0).toString()) }
    var specificDates by remember {
        mutableStateOf(
            (0 until (task?.optJSONArray("specific_dates")?.length() ?: 0))
                .map { task!!.optJSONArray("specific_dates")!!.optString(it) }
                .joinToString(",")
        )
    }
    var sequenceMode by remember { mutableStateOf(task?.optString("sequence_mode")?.ifBlank { "sequential" } ?: "sequential") }
    var folderMode by remember { mutableStateOf(task?.optString("folder_mode")?.ifBlank { "sequential" } ?: "sequential") }
    var folderCount by remember { mutableStateOf((task?.optInt("folder_play_count", 1) ?: 1).toString()) }
    var marker by remember { mutableStateOf((task?.optInt("tiaoxiu_marker", 0) ?: 0).toString()) }
    var stopMode by remember { mutableStateOf(task?.optString("stop_mode")?.ifBlank { "immediate" } ?: "immediate") }
    var selectedDays by remember {
        mutableStateOf(
            (0 until (task?.optJSONArray("days")?.length() ?: 0))
                .map { task!!.optJSONArray("days")!!.optInt(it) }
                .filter { it in 1..7 }
                .toSet()
        )
    }
    var selectedIds by remember {
        mutableStateOf(
            (0 until (task?.optJSONArray("music_ids")?.length() ?: 0))
                .map { task!!.optJSONArray("music_ids")!!.optString(it) }
                .toSet()
        )
    }
    val music = state.musicJson

    AppDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (isNew) "新增任务" else "编辑任务 #${task?.optInt("id")}") },
        text = {
            // 注意：**这里不能再套 verticalScroll** —— AppDialog 已经提供了"限高可滚"的内容区，
            // 再嵌一层会触发 Compose 的"竖直可滚动组件被给了无限高约束"→ 点编辑就闪退（真机踩过）。
            Column {
                AppField(
                    value = time,
                    onValueChange = { v -> time = v.take(5) },
                    label = { Text("开始时间（HH:mm）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(6.dp))
                Text("星期（不选 = 只按指定日期，通常至少要选一天）", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    for (d in 1..7) {
                        val on = selectedDays.contains(d)
                        Box(
                            Modifier
                                .size(34.dp)
                                .clip(RoundedCornerShape(9.dp))
                                .background(if (on) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceVariant)
                                .clickableNoRipple { selectedDays = if (on) selectedDays - d else selectedDays + d },
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                arrayOf("一", "二", "三", "四", "五", "六", "日")[d - 1],
                                fontSize = 13.sp,
                                color = if (on) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface
                            )
                        }
                    }
                }
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    AppField(
                        value = durationM,
                        onValueChange = { v -> durationM = v.filter { it.isDigit() }.take(4) },
                        label = { Text("时长(分)") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                    AppField(
                        value = durationS,
                        onValueChange = { v -> durationS = v.filter { it.isDigit() }.take(2) },
                        label = { Text("秒") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                    AppField(
                        value = volume,
                        onValueChange = { v -> volume = v.filter { it.isDigit() }.take(3) },
                        label = { Text("音量%") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                }
                Text("时长 0 分 0 秒 = 整曲播完", fontSize = 10.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(8.dp))
                // 指定日期（与电脑端 specific_dates 一致；留空表示只按上面的星期）
                AppField(
                    value = specificDates,
                    onValueChange = { v -> specificDates = v.take(120) },
                    label = { Text("指定日期（逗号分隔，如 2026-10-01,10-02；留空按星期）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                Text("播放顺序", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    EqualOptionRow(
                        options = listOf(
                            Triple("sequential", "顺序", "list"),
                            Triple("random", "随机", "shuffle")
                        ),
                        selected = sequenceMode,
                        onSelect = { sequenceMode = it }
                    )
                }
                Spacer(Modifier.height(8.dp))
                Text("目录播法（曲目是目录时生效）", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    EqualOptionRow(
                        options = listOf(
                            Triple("sequential", "顺序", "list"),
                            Triple("random", "随机", "shuffle")
                        ),
                        selected = folderMode,
                        onSelect = { folderMode = it }
                    )
                    AppField(
                        value = folderCount,
                        onValueChange = { v -> folderCount = v.filter { it.isDigit() }.take(3) },
                        label = { Text("提歌数") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                    // 调休标记与提歌数同一行（用户要求：两个都是小数字，没必要各占一行）
                    AppField(
                        value = marker,
                        onValueChange = { v -> marker = v.filter { it.isDigit() }.take(2).ifBlank { "1" } },
                        label = { Text("调休标记 0-10") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                }

                Spacer(Modifier.height(8.dp))
                Text("到达时间时（上一个任务还在放）", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    EqualOptionRow(
                        options = listOf(
                            Triple("immediate", "立即切掉", "stop"),
                            Triple("finish_track", "播完这首", "forward-step")
                        ),
                        selected = stopMode,
                        onSelect = { stopMode = it }
                    )
                }
                Spacer(Modifier.height(8.dp))
                Text("曲目（点一下选中/取消；前面的数字 = 播放顺序）", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                if (music == null || music.length() == 0) {
                    Text("（还没拿到音乐库，先点「刷新数据」）", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                } else {
                    Column {
                        for (i in 0 until minOf(music.length(), 40)) {
                            val o = music.optJSONObject(i) ?: continue
                            val id = o.optString("id")
                            if (id.isBlank()) continue
                            val on = selectedIds.contains(id)
                            Row(
                                Modifier
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(8.dp))
                                    .clickableNoRipple { selectedIds = if (on) selectedIds - id else selectedIds + id }
                                    .padding(vertical = 5.dp, horizontal = 4.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                if (on) {
                                    // 已选：显示**播放顺序号**（1、2、3…），一眼看出会按什么顺序播
                                    Box(
                                        Modifier
                                            .size(16.dp)
                                            .clip(CircleShape)
                                            .background(MaterialTheme.colorScheme.primary),
                                        contentAlignment = Alignment.Center
                                    ) {
                                        Text(
                                            "${selectedIds.indexOf(id) + 1}",
                                            fontSize = 9.5.sp,
                                            color = MaterialTheme.colorScheme.onPrimary,
                                            textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                                            lineHeight = 10.sp,
                                            modifier = Modifier.fillMaxWidth()
                                        )
                                    }
                                } else {
                                    FaIcon(
                                        "circle-question",
                                        size = 13.dp,
                                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    "#$id　${o.optString("name")}",
                                    fontSize = 12.sp,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = {
                val fields = HashMap<String, Any?>()
                fields["start_time"] = if (time.length == 5) "$time:00" else time
                fields["days"] = JSONArray(selectedDays.sorted())
                fields["volume"] = volume.toIntOrNull() ?: 100
                fields["duration_m"] = durationM.toIntOrNull() ?: 0
                fields["duration_s"] = durationS.toIntOrNull() ?: 0
                fields["music_ids"] = JSONArray(selectedIds.toList())
                fields["sequence_mode"] = sequenceMode
                fields["folder_mode"] = folderMode
                fields["folder_play_count"] = folderCount.toIntOrNull() ?: 1
                fields["tiaoxiu_marker"] = marker.toIntOrNull() ?: 0
                fields["stop_mode"] = stopMode
                // 指定日期：逗号/空格分隔，逐条清洗（电脑端语义是字符串数组）
                val sd = JSONArray()
                specificDates.split(',', '，', ' ').map { it.trim() }.filter { it.isNotEmpty() }.forEach { sd.put(it) }
                fields["specific_dates"] = sd
                state.saveTask(task?.optInt("id"), fields)
                onDismiss()
            }) { FaIcon("floppy-disk", size = 13.dp, tint = MaterialTheme.colorScheme.primary); Spacer(Modifier.width(6.dp)); Text("保存") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { FaIcon("arrow-left", size = 13.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant); Spacer(Modifier.width(6.dp)); Text("取消") } }
    )
}

/** 今天会触发的任务（星期命中，或 specific_dates 里含今天）。 */
private fun todayTasks(arr: JSONArray): List<JSONObject> {
    val cal = Calendar.getInstance()
    val dow = cal.get(Calendar.DAY_OF_WEEK) // 1=周日
    val ourDow = if (dow == 1) 7 else dow - 1
    val today = String.format("%04d-%02d-%02d", cal.get(Calendar.YEAR), cal.get(Calendar.MONTH) + 1, cal.get(Calendar.DAY_OF_MONTH))
    val out = ArrayList<JSONObject>()
    for (i in 0 until arr.length()) {
        val o = arr.optJSONObject(i) ?: continue
        val days = o.optJSONArray("days")
        var hit = false
        if (days != null) for (j in 0 until days.length()) if (days.optInt(j) == ourDow) { hit = true; break }
        if (!hit) {
            val sd = o.optJSONArray("specific_dates")
            if (sd != null) for (j in 0 until sd.length()) {
                val s = sd.optString(j)
                if (s.contains(today) || today.endsWith(s)) { hit = true; break }
            }
        }
        if (hit) out.add(o)
    }
    return out
}

// ===================== 调休（可编辑） =====================

@Composable
fun HolidayScreen(state: AppState) {
    var editingIndex by remember { mutableStateOf(-1) }
    var creating by remember { mutableStateOf(false) }
    PageColumn {
        if (!state.dataReady) NeedData(state)
        AppCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle("调休规则", Modifier.weight(1f))
                // 云端订阅同步（电脑端 /api/tiaoxiu/sync，与电脑端"同步云端调休"是同一个接口）
                IconAction("cloud-arrow-down", "同步云端调休", state.dataReady && state.isAdminDevice, compact = true, hideWhenDisabled = true) { state.syncTiaoxiu() }
                Spacer(Modifier.width(6.dp))
                IconAction("calendar-plus", "新增调休", state.dataReady && state.isAdminDevice, compact = true, hideWhenDisabled = true) { creating = true }
            }
            Spacer(Modifier.height(6.dp))
            val arr = state.holidayJson
            if (arr == null) EmptyHint("正在获取…")
            else if (arr.length() == 0) EmptyHint("还没有调休规则：点右上角「＋」新增")
            else for (i in 0 until minOf(arr.length(), 60)) {
                val o = arr.optJSONObject(i) ?: continue
                val action = o.optString("action")
                // 电脑端的调休记录有两种写法：单日用 date，区间用 start_date/end_date（云端订阅导入的也是）。
                // 以前只读 date → 区间记录在列表里**日期是空的**（用户反馈"调休主界面不显示日期"）。
                val single = o.optString("date").ifBlank { o.optString("start_date") }
                val endD = o.optString("end_date")
                val date = when {
                    single.isBlank() -> "（日期缺失，请编辑补上）"
                    endD.isNotBlank() && endD != single -> "$single 至 $endD"
                    else -> single
                }
                val marker = o.optInt("marker", 0)
                val note = o.optString("note")
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    FaIcon(
                        if (action == "disable") "calendar-check" else "calendar-days",
                        size = 14.dp,
                        tint = if (action == "disable") Color(0xFFEE4B6A) else Color(0xFF2AA45C)
                    )
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text(date, fontSize = 13.sp)
                        // 第二行：左边"停用/启用 + 备注"，**标记放行末且恢复彩色**（用户要求：原来有颜色，放行末）
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                (if (action == "disable") "停用（不播音）" else "启用（照常打铃）") +
                                    (if (note.isNotBlank()) " · $note" else ""),
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                softWrap = false,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f)
                            )
                            if (marker > 0) {
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    "标记 $marker",
                                    fontSize = 10.sp,
                                    color = MaterialTheme.colorScheme.primary,
                                    maxLines = 1,
                                    softWrap = false
                                )
                            }
                        }
                    }
                    IconAction("pen-to-square", "编辑", state.dataReady && state.isAdminDevice, compact = true, hideWhenDisabled = true) { editingIndex = i }
                    Spacer(Modifier.width(6.dp))
                    IconAction("trash", "删除", state.dataReady && state.isAdminDevice, compact = true, hideWhenDisabled = true) { state.removeTiaoxiu(i, o.toString()) }   // 失败原因由 AppState 写进日志（读不到表就拒绝，避免误清）
                }
                if (i < arr.length() - 1) ThinDivider()
            }
        }
        AppCard {
            SectionTitle("说明")
            Spacer(Modifier.height(4.dp))
            Text(
                "与电脑端、网页同一份调休表；手机端保存是「整表替换」（电脑端接口本来就是这个语义），" +
                    "所以每次都会把当前全部条目一起提交，不会误删。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
    val arr = state.holidayJson
    // 新增不需要已有数据（数据没加载也能先建）；编辑才需要对应记录
    if (creating || (editingIndex >= 0 && arr != null)) {
        HolidayEditDialog(
            state = state,
            index = editingIndex,
            item = if (editingIndex >= 0) arr?.optJSONObject(editingIndex) else null,
            onDismiss = { creating = false; editingIndex = -1 }
        )
    }
}

@Composable
private fun HolidayEditDialog(state: AppState, index: Int, item: JSONObject?, onDismiss: () -> Unit) {
    var date by remember { mutableStateOf(item?.optString("date")?.ifBlank { item.optString("start_date") } ?: todayIso()) }
    var endDate by remember { mutableStateOf(item?.optString("end_date") ?: "") }
    var action by remember { mutableStateOf(item?.optString("action") ?: "disable") }
    var note by remember { mutableStateOf(item?.optString("note") ?: "") }
    var marker by remember { mutableStateOf(((item?.optInt("marker", 1) ?: 1).coerceIn(1, 10)).toString()) }   // 用户确认：编辑器只给 1-10，0 是「调休管理」专用

    AppDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (index < 0) "新增调休" else "编辑调休") },
        text = {
            Column {
                AppField(
                    value = date,
                    onValueChange = { v -> date = v.take(10) },
                    label = { Text("开始日期（YYYY-MM-DD）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(6.dp))
                // 日期区间：电脑端记录用 start_date/end_date（留空表示只一天）
                AppField(
                    value = endDate,
                    onValueChange = { v -> endDate = v.take(10) },
                    label = { Text("结束日期（可留空 = 只这一天）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                Text("动作", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    EqualOptionRow(
                        options = listOf(
                            Triple("disable", "停用", "stop"),
                            Triple("enable", "启用", "calendar-check")
                        ),
                        selected = action,
                        onSelect = { action = it },
                        compact = true
                    )
                }
                Spacer(Modifier.height(8.dp))
                AppField(
                    value = note,
                    onValueChange = { v -> note = v.take(30) },
                    label = { Text("备注（如：劳动节）") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(8.dp))
                AppField(
                    value = marker,
                    onValueChange = { v -> marker = v.filter { it.isDigit() }.take(2).ifBlank { "1" } },
                    label = { Text("标记 1-10（0 是「调休管理」专用）") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth()
                )
            }
        },
        confirmButton = {
            TextButton(onClick = {
                state.upsertTiaoxiu(index, date, action, note, marker.toIntOrNull() ?: 0, endDate)
                onDismiss()
            }) { FaIcon("floppy-disk", size = 13.dp, tint = MaterialTheme.colorScheme.primary); Spacer(Modifier.width(6.dp)); Text("保存") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { FaIcon("arrow-left", size = 13.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant); Spacer(Modifier.width(6.dp)); Text("取消") } }
    )
}

private fun todayIso(): String {
    val c = Calendar.getInstance()
    return String.format("%04d-%02d-%02d", c.get(Calendar.YEAR), c.get(Calendar.MONTH) + 1, c.get(Calendar.DAY_OF_MONTH))
}

// ===================== 设置 =====================

@Composable
fun SettingsScreen(state: AppState) {
    var manual by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<AppState.DeviceEntry?>(null) }
    var deleting by remember { mutableStateOf<AppState.DeviceEntry?>(null) }
    PageColumn {
        // 管理员面板（含广播总开关、配对码、话筒设备列表）：普通设备整块隐藏。
        // 用户要求：总开关也收进管理员区，跟配对码/设备列表一起隐藏，不再单独占一张卡片。
        AdminPanel(state)
        AppCard {
            SectionTitle("外观 · 主题色（点色块即换，不用看文字）")
            Spacer(Modifier.height(10.dp))
            // 【排版】六个色块一行放不下（窄屏会把最后一个"玫瑰"挤扁/挤出屏幕）。
            // 现在分成两行、每行三个；选中只加描边+对勾，**不再改变尺寸**（尺寸一变就会挤到邻居）。
            THEME_PRESETS.chunked(3).forEachIndexed { rowIdx, row ->
                if (rowIdx > 0) Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                    row.forEach { p ->
                        val on = p.key == state.themeColor
                        Box(
                            Modifier
                                .size(40.dp)
                                .clip(CircleShape)
                                .background(p.color)
                                .border(
                                    width = if (on) 3.dp else 1.dp,
                                    color = if (on) MaterialTheme.colorScheme.onSurface else Color.White.copy(alpha = 0.5f),
                                    shape = CircleShape
                                )
                                .clickableNoRipple { state.applyThemeColor(p.key) },
                            contentAlignment = Alignment.Center
                        ) {
                            if (on) FaIcon("check", size = 16.dp, tint = Color.White)
                        }
                    }
                }
            }
            Spacer(Modifier.height(8.dp))
            Text(
                "界面固定 MIUIX 风格，底部导航栏固定液态玻璃；只换主题色。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        // 界面缩放（用户要求：放**设置**里，做成**条状**滑条）
        Spacer(Modifier.height(8.dp))
        ThinDivider()
        Spacer(Modifier.height(8.dp))
        SectionTitle("界面缩放")
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("小", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.weight(1f))
            Text("${(state.uiScale * 100).toInt()}%", fontSize = 12.sp, color = MaterialTheme.colorScheme.primary)
            Spacer(Modifier.weight(1f))
            Text("大", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        AppSlider(
            fraction = ((state.uiScale - 0.85f) / 0.45f).coerceIn(0f, 1f),
            onFraction = { f ->
                val v = 0.85f + f * 0.45f
                state.applyUiScale(kotlin.math.round(v / 0.05f) * 0.05f)   // 按 5% 一档吸附，手感稳
            }
        )
        Text("拖动即可整体缩放界面（布局与字号一起变），重启后保持", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        // 广播系统设备列表（用户要求：以主机名区分，同名覆盖、异名新增；可设外显名称，有编辑/删除）
        AppCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SectionTitle("广播系统（${state.devices.size} 台）", Modifier.weight(1f))
                IconAction("qrcode", "扫码添加", true, compact = true) { state.showScan = true }
            }
            Spacer(Modifier.height(6.dp))
            if (state.devices.isEmpty()) {
                EmptyHint("还没有设备：点右上角「＋」→『扫码配置』扫一台主机的二维码")
            } else {
                state.devices.sortedByDescending { it.lastUsedAt }.forEach { d ->
                    val inUse = d.key == state.currentDeviceKey
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 5.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        FaIcon(
                            if (inUse) "tower-broadcast" else "server",
                            size = 15.dp,
                            tint = if (inUse) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(
                                d.alias + if (inUse) "（当前）" else "",
                                fontSize = 13.sp,
                                fontWeight = if (inUse) FontWeight.Medium else FontWeight.Normal,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                "主机名 ${d.hostName}" + (if (d.alias != d.hostName) " · 外显名 ${d.alias}" else ""),
                                fontSize = 10.5.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                        }
                        if (!inUse) {
                            IconAction("circle-play", "切换到这台", true, compact = true) { state.switchToDevice(d.key) }
                            Spacer(Modifier.width(4.dp))
                        }
                        IconAction("pen-to-square", "改外显名称", true, compact = true) { renaming = d }
                        Spacer(Modifier.width(4.dp))
                        IconAction("trash", "删除", true, compact = true) { deleting = d }
                    }
                    if (d != state.devices.sortedByDescending { it.lastUsedAt }.last()) ThinDivider()
                }
            }
            Spacer(Modifier.height(6.dp))
            Text(
                "以主机名区分：同名（同一台电脑）扫码会自动覆盖它的连接配置，异名则新增一条。点圆点图标即可切换过去（会重新组网，话筒需重新配对）。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        AppCard {
            SectionTitle("连接配置（扫一次就记住）")
            Spacer(Modifier.height(6.dp))
            InfoRow("服务器", state.vntServer.ifBlank { "—" })
            InfoRow("组网令牌", state.vntToken.ifBlank { "—" })
            InfoRow("数据密码", state.dataPassword.ifBlank { "—" })
            InfoRow("协议/加密", "${state.protocol} / ${state.model}" + if (state.useFinger) " · 指纹" else "")
            InfoRow("我的虚拟IP", state.myVirtualIp.ifBlank { "—" })
            InfoRow("电脑虚拟IP", state.pcVntIp.ifBlank { "—" })
            InfoRow("电脑局域网IP", state.pcLanIp.ifBlank { "（无，只走 vnt 中继）" })
            InfoRow("实测可用", if (state.activePcVip != 0) com.broadcast.vntlite.VntPacket.intToIp(state.activePcVip) else "配对后自动确定")
            InfoRow("端口", "音频 ${state.audioPort} · 配对 ${state.ctlPort} · 网页 ${state.httpPort}")
            InfoRow("主机名", state.hostName.ifBlank { "—" })
            InfoRow("本机设备ID", state.phoneDeviceId.take(8) + "…（固定不变）")
            Spacer(Modifier.height(4.dp))
            ThinDivider()
            Spacer(Modifier.height(8.dp))
            // 组网开关（用户要求：启动/停止合并成一个开关；开=启动并自动重连，关=停止）
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                FaIcon("tower-broadcast", size = 15.dp,
                    tint = if (state.connected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(8.dp))
                Column(Modifier.weight(1f)) {
                    Text("组网", fontSize = 13.sp)
                    Text(
                        if (state.connected) "已启动" + (if (state.registered) "（已入网 ${state.myVirtualIp}）" else "（连接中…）")
                        else "已停止",
                        fontSize = 10.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                MiuixSwitchFor(state.connected) { on ->
                    if (on) { if (!state.connected) state.startNetwork { } } else state.stopNetwork()
                }
            }
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                AutoFitButtonRow(
                    listOf(
                        BtnSpec("立即重试", "arrows-rotate", state.configured) { state.restartNow() },
                        BtnSpec("刷新数据", "rotate", state.dataReady) { state.refreshAll() }
                    ),
                    forceEqual = true   // 用户要求：这两个按钮必须一样宽（否则"立即重试"明显更宽）
                )
            }
            Spacer(Modifier.height(6.dp))
            Text(
                "扫码入口在右上角「＋」。手动配置适合二维码不好扫的时候：照着电脑端「vnt 设置」窗把服务器/令牌/数据密码/电脑虚拟 IP 填进来即可。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        AppCard {
            SectionTitle("数据通道")
            Spacer(Modifier.height(6.dp))
            InfoRow("状态", state.dataStatus)
            InfoRow("最后更新", if (state.lastDataAt > 0) android.text.format.DateFormat.format("HH:mm:ss", state.lastDataAt).toString() else "—")
            Spacer(Modifier.height(6.dp))
            Text(
                "播控/排程/调休走这条通道：只要入网 + 电脑端填了数据密码就能用，**不需要 6 位动态码**。" +
                    "动态码只用于话筒喊话（见总览页的「送话」按钮）。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        AppCard {
            SectionTitle("诊断")
            Spacer(Modifier.height(6.dp))
            InfoRow("构建", APP_BUILD)
            InfoRow("组网", if (state.registered) "已入网 ${state.myVirtualIp}" else "未入网")
            InfoRow("话筒", state.micStatus)
            Spacer(Modifier.height(8.dp))
            Text(
                "遇到问题时点下面按钮导出日志：会带上运行状态、连接配置（密钥已脱敏）和最近 300 条操作日志，" +
                    "可以直接发到微信「文件传输助手」，一步到电脑，不用手打粘贴。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(8.dp))
            ExportLogButton(state)
        }

        AppCard {
            SectionTitle("关于")
            Spacer(Modifier.height(6.dp))
            InfoRow("版本", "v$APP_VERSION")
            InfoRow("构建", APP_BUILD)
            InfoRow("协议", "vnt-lite（不建虚拟网卡 / 不需要管理员）")
            Spacer(Modifier.height(6.dp))
            Text(
                "界面基于 MIUIX（Apache-2.0）；图标全部为内置 Font Awesome；液态玻璃为自绘实现。" +
                    "数据来自电脑端现成 HTTP 接口（cbs-api 加密转发）。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
    if (manual) ManualConfigDialog(state) { manual = false }
    // 改外显名称 / **编辑连接配置**（用户要求：设备行的编辑按钮取代下面的"手动配置"）
    renaming?.let { d ->
        var name by remember(d.key) { mutableStateOf(d.alias) }
        val parsed = remember(d.key) { com.broadcast.app.core.ScanConfigParser.parse(d.qr) }
        var server by remember(d.key) { mutableStateOf(parsed?.server ?: "") }
        var token by remember(d.key) { mutableStateOf(parsed?.token ?: "") }
        var pw by remember(d.key) { mutableStateOf(parsed?.wPassword ?: "") }
        var vip by remember(d.key) { mutableStateOf(parsed?.vntIp ?: "") }
        var lan by remember(d.key) { mutableStateOf(parsed?.lanIp ?: "") }
        var audioPort by remember(d.key) { mutableStateOf((parsed?.audioPort ?: 29880).toString()) }
        var ctlPort by remember(d.key) { mutableStateOf((parsed?.ctlPort ?: 29881).toString()) }
        var httpPort by remember(d.key) { mutableStateOf((parsed?.httpPort ?: 5000).toString()) }
        AppDialog(
            onDismissRequest = { renaming = null },
            title = { Text("编辑广播系统") },
            text = {
                // 同上：弹层里不能再套 verticalScroll（AppDialog 已提供），否则嵌套滚动会闪退
                Column {
                    Text("主机名：${d.hostName}（用它区分是不是同一台，同名扫码会覆盖）", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.height(8.dp))
                    AppField(name, { v -> name = v.take(24) }, label = { Text("外显名称（例如：教学楼一楼）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(6.dp))
                    AppField(server, { v -> server = v.trim() }, label = { Text("服务器 host:port") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(6.dp))
                    AppField(token, { v -> token = v.trim() }, label = { Text("组网令牌") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(6.dp))
                    AppField(pw, { v -> pw = v.trim() }, label = { Text("数据密码（配对/数据通道加密）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(6.dp))
                    AppField(vip, { v -> vip = v.trim() }, label = { Text("电脑虚拟 IP（mic.vnt_ip）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(6.dp))
                    AppField(lan, { v -> lan = v.trim() }, label = { Text("电脑局域网 IP（可选）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Spacer(Modifier.height(6.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        AppField(audioPort, { v -> audioPort = v.filter { c -> c.isDigit() } }, label = { Text("音频端口") }, singleLine = true, modifier = Modifier.weight(1f))
                        AppField(ctlPort, { v -> ctlPort = v.filter { c -> c.isDigit() } }, label = { Text("配对端口") }, singleLine = true, modifier = Modifier.weight(1f))
                        AppField(httpPort, { v -> httpPort = v.filter { c -> c.isDigit() } }, label = { Text("网页端口") }, singleLine = true, modifier = Modifier.weight(1f))
                    }
                    Spacer(Modifier.height(6.dp))
                    Text("令牌与数据密码必须和电脑端「vnt 设置」里的一致；改完点保存即写入这台设备的记录。", fontSize = 10.5.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    state.updateDeviceConfig(
                        d.key, name, server, token, pw, vip, lan,
                        audioPort.toIntOrNull() ?: 29880,
                        ctlPort.toIntOrNull() ?: 29881,
                        httpPort.toIntOrNull() ?: 5000
                    )
                    renaming = null
                }) { FaIcon("floppy-disk", size = 13.dp, tint = MaterialTheme.colorScheme.primary); Spacer(Modifier.width(6.dp)); Text("保存") }
            },
            dismissButton = { TextButton(onClick = { renaming = null }) { FaIcon("arrow-left", size = 13.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant); Spacer(Modifier.width(6.dp)); Text("取消") } }
        )
    }
    // 删除确认
    deleting?.let { d ->
        AppDialog(
            onDismissRequest = { deleting = null },
            title = { Text("删除这台广播系统？") },
            text = { Text("「${d.alias}」（主机 ${d.hostName}）的连接配置会从手机里删掉，之后再连需要重新扫码。") },
            confirmButton = { TextButton(onClick = { state.removeDevice(d.key); deleting = null }) { FaIcon("trash", size = 13.dp, tint = MaterialTheme.colorScheme.error); Spacer(Modifier.width(6.dp)); Text("删除") } },
            dismissButton = { TextButton(onClick = { deleting = null }) { FaIcon("arrow-left", size = 13.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant); Spacer(Modifier.width(6.dp)); Text("取消") } }
        )
    }
}

/** 手动配置：二维码扫不出来时的兜底（字段与二维码一一对应）。 */
@Composable
private fun ManualConfigDialog(state: AppState, onDismiss: () -> Unit) {
    var server by remember { mutableStateOf(state.vntServer) }
    var token by remember { mutableStateOf(state.vntToken) }
    var pw by remember { mutableStateOf(state.dataPassword) }
    var vip by remember { mutableStateOf(state.pcVntIp) }
    var lan by remember { mutableStateOf(state.pcLanIp) }
    var ctl by remember { mutableStateOf(state.ctlPort.toString()) }
    var audio by remember { mutableStateOf(state.audioPort.toString()) }
    var http by remember { mutableStateOf(state.httpPort.toString()) }

    AppDialog(
        onDismissRequest = onDismiss,
        title = { Text("手动配置") },
        text = {
            // 注意：**这里不能再套 verticalScroll** —— AppDialog 已经提供了"限高可滚"的内容区，
            // 再嵌一层会触发 Compose 的"竖直可滚动组件被给了无限高约束"→ 点编辑就闪退（真机踩过）。
            Column {
                AppField(server, { server = it.trim() }, label = { Text("服务器 host:port") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(6.dp))
                AppField(token, { token = it.trim() }, label = { Text("组网令牌") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(6.dp))
                AppField(pw, { pw = it.trim() }, label = { Text("数据密码（配对/数据通道加密）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(6.dp))
                AppField(vip, { vip = it.trim() }, label = { Text("电脑虚拟 IP（二维码 mic.vnt_ip）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(6.dp))
                AppField(lan, { lan = it.trim() }, label = { Text("电脑局域网 IP（可选）") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(6.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    AppField(audio, { audio = it.filter { c -> c.isDigit() } }, label = { Text("音频端口") }, singleLine = true, modifier = Modifier.weight(1f))
                    AppField(ctl, { ctl = it.filter { c -> c.isDigit() } }, label = { Text("配对端口") }, singleLine = true, modifier = Modifier.weight(1f))
                    AppField(http, { http = it.filter { c -> c.isDigit() } }, label = { Text("网页端口") }, singleLine = true, modifier = Modifier.weight(1f))
                }
                Spacer(Modifier.height(6.dp))
                Text("提示：令牌与数据密码必须和电脑端完全一致；电脑端「vnt 设置」窗里能看到这两个值。", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        confirmButton = {
            TextButton(onClick = {
                state.applyManualConfig(
                    server, token, pw, vip, lan,
                    audio.toIntOrNull() ?: 29880,
                    ctl.toIntOrNull() ?: 29881,
                    http.toIntOrNull() ?: 5000
                )
                onDismiss()
            }) { Text("保存并组网") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { FaIcon("arrow-left", size = 13.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant); Spacer(Modifier.width(6.dp)); Text("取消") } }
    )
}

/**
 * 「导出日志」按钮：把界面日志 + 运行状态 + 脱敏后的配置写成一个 txt，再拉起系统分享
 * （可以直接发到微信「文件传输助手」，一步到电脑，不用手打粘贴）。
 * 任何失败都只记一行日志，不闪退。
 */
@Composable
private fun ExportLogButton(state: AppState) {
    val context = LocalContext.current
    AppOutlinedButton("导出日志（可发微信）", {
        try {
            val f = LogExport.write(context, LogExport.build(state))
            state.log("已导出日志：" + f.name)
            LogExport.share(context, f)
        } catch (t: Throwable) {
            state.log("导出日志失败：" + (t.message ?: t.javaClass.simpleName))
        }
    }, icon = "file-export")
}

/**
 * **管理员面板**（用户要求：放手机「设置」页顶部，只对管理员显示）：
 *   ① 广播总开关（紧急停用，放最上面一眼能找到）；
 *   ② 当前 6 位配对码（60 秒一换，带剩余秒数与进度条）；
 *   ③ 话筒设备列表 + 踢出。
 *
 * 是否显示由电脑端门禁决定：调 `/api/mic/code` 成功 = 管理员；被拒 = **整块隐藏**
 * （普通设备连总开关都看不到、也点不到）。电脑端 MicApiBridge 对写接口还有第二道门禁，
 * 所以即使有人绕过界面直接发请求也会被拒。
 */
@Composable
private fun AdminPanel(state: AppState) {
    LaunchedEffect(Unit) {
        while (true) {
            state.refreshAdminPanel()
            kotlinx.coroutines.delay(5000)      // 5 秒刷新一次（配对码 60 秒一换，够用且省流量）
        }
    }
    if (!state.isAdminDevice) return
    AppCard {
        // ---------- 标题行 ----------
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            FaIcon("user-shield", size = 15.dp, tint = MaterialTheme.colorScheme.primary)
            Spacer(Modifier.width(8.dp))
            Text(
                "管理员",
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f)
            )
            // 小徽章：一眼看出"这些操作只在这台设备上可用"
            Text(
                "仅本机",
                fontSize = 10.sp,
                color = MaterialTheme.colorScheme.primary,
                modifier = Modifier
                    .clip(RoundedCornerShape(6.dp))
                    .background(MaterialTheme.colorScheme.primary.copy(alpha = 0.12f))
                    .padding(horizontal = 7.dp, vertical = 2.dp)
            )
        }
        Spacer(Modifier.height(14.dp))

        // ---------- ① 广播总开关 ----------
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("广播总开关", fontSize = 14.sp, fontWeight = FontWeight.Medium)
                Text(
                    if (state.globalOn) "运行中：定时打铃照常执行" else "已停用：不打铃，所有音频静音",
                    fontSize = 11.sp,
                    color = if (state.globalOn) MaterialTheme.colorScheme.onSurfaceVariant
                            else MaterialTheme.colorScheme.error
                )
            }
            MiuixSwitchFor(state.globalOn) { on -> state.trySetGlobalSwitch(on) }
        }
        Spacer(Modifier.height(6.dp))
        Text(
            "停用后电脑端不会打铃、也不播放任何音频；恢复后照常按排程执行。",
            fontSize = 10.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )

        Spacer(Modifier.height(14.dp))
        ThinDivider()
        Spacer(Modifier.height(12.dp))

        // ---------- ② 配对码 ----------
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                "话筒配对码",
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f)
            )
            Text("${state.micCodeLeft}s 后换码", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(6.dp))
            AppOutlinedButton("刷新", { state.refreshAdminPanel() }, icon = "rotate")
        }
        Spacer(Modifier.height(8.dp))
        // 码本体：等宽 + 大字 + 字距，三位一组更好念给对面听
        val shown = state.micCode.ifBlank { "------" }
        val pretty = if (shown.length == 6) shown.substring(0, 3) + " " + shown.substring(3) else shown
        Text(
            pretty,
            fontSize = 34.sp,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.primary,
            letterSpacing = 4.sp,
            modifier = Modifier.fillMaxWidth(),
            textAlign = TextAlign.Center
        )
        Spacer(Modifier.height(8.dp))
        // 剩余时间进度条：比纯数字更直观地提示"快换码了，抓紧输"
        val period = 60f
        val frac = (state.micCodeLeft.coerceIn(0, 60) / period)
        Box(
            Modifier
                .fillMaxWidth()
                .height(4.dp)
                .clip(RoundedCornerShape(2.dp))
                .background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.15f))
        ) {
            Box(
                Modifier
                    .fillMaxWidth(frac)
                    .height(4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(
                        if (frac < 0.25f) MaterialTheme.colorScheme.error
                        else MaterialTheme.colorScheme.primary
                    )
            )
        }
        Spacer(Modifier.height(6.dp))
        Text(
            "电脑端「运行总览」显示的是同一个码；同一窗口只能配对一次，换码后旧码立即失效。",
            fontSize = 10.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )

        Spacer(Modifier.height(14.dp))
        ThinDivider()
        Spacer(Modifier.height(12.dp))

        // ---------- ③ 话筒设备 ----------
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                "话筒设备",
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.weight(1f)
            )
            Text(
                "在线 ${state.micDevices.size} 台",
                fontSize = 11.sp,
                color = if (state.micDevices.isEmpty()) MaterialTheme.colorScheme.onSurfaceVariant
                        else MaterialTheme.colorScheme.primary
            )
        }
        Spacer(Modifier.height(6.dp))
        if (state.micDevices.isEmpty()) {
            Text(
                "当前没有设备连着话筒",
                fontSize = 11.5.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        state.micDevices.forEach { d ->
            val isAdminDev = d.optBoolean("admin")
            Row(
                Modifier
                    .fillMaxWidth()
                    .padding(vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                // 状态点：在线绿点，管理员用主色
                Box(
                    Modifier
                        .size(7.dp)
                        .clip(CircleShape)
                        .background(if (isAdminDev) MaterialTheme.colorScheme.primary else SuccessDot)
                )
                Spacer(Modifier.width(8.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        d.optString("device").take(16).ifBlank { "未知设备" },
                        fontSize = 12.5.sp,
                        maxLines = 1,
                        softWrap = false,
                        overflow = TextOverflow.Ellipsis
                    )
                    Text(
                        "会话 ${d.optInt("sid")}　${d.optString("ip")}" + if (isAdminDev) "　管理员" else "",
                        fontSize = 10.5.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        softWrap = false,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                // 管理员设备不显示"踢出"（用户要求）：能把设备设为管理员的本来就是受信任设备。
                if (!isAdminDev) {
                    AppOutlinedButton(
                        "踢出",
                        { state.kickMicDevice(d.optInt("sid"), d.optString("device")) },
                        icon = "stop"
                    )
                }
            }
        }
    }
    Spacer(Modifier.height(12.dp))
}

/** 设备在线状态点颜色（与主题无关的固定绿，保证一眼可辨）。 */
private val SuccessDot = Color(0xFF34C759)


/** 普通权限提示条（用户要求：让"只读"状态一眼可见）。 */
@Composable
private fun ReadOnlyHint(text: String) {
    Row(
        Modifier.fillMaxWidth().padding(top = 6.dp, bottom = 2.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        FaIcon("lock", size = 12.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(6.dp))
        Text(text, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
