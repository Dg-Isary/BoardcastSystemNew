package com.broadcast.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import top.yukonga.miuix.kmp.basic.Button as MiuixButton
import top.yukonga.miuix.kmp.basic.ButtonDefaults as MiuixButtonDefaults

import top.yukonga.miuix.kmp.basic.Card as MiuixCard
import top.yukonga.miuix.kmp.basic.Text as MiuixText

/**
 * App 统一的**底部滑出面板**（用户要求：编辑任务 / 调休 / 右上角「＋」菜单都改成底部面板，风格与整体一致）。
 *
 * 参数名与 Material3 的 `AlertDialog` **完全一致**，所以调用点只需要改一个词（`AlertDialog(` → `AppDialog(`），
 * 三个窗口一次性统一成：圆角玻璃卡片、从底部滑出、按钮在面板右下 —— 不再用系统白底直角对话框。
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun AppDialog(
    onDismissRequest: () -> Unit,
    title: (@Composable () -> Unit)? = null,
    text: (@Composable () -> Unit)? = null,
    confirmButton: @Composable () -> Unit = {},
    dismissButton: (@Composable () -> Unit)? = null
) {
    androidx.compose.material3.ModalBottomSheet(
        onDismissRequest = onDismissRequest,
        containerColor = MaterialTheme.colorScheme.surface,
        scrimColor = Color.Black.copy(alpha = 0.30f),
        shape = RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp),
        // 弹层"跟手"：跳过"半展开"中间态，一拉就到位（用户反馈弹出不够跟手）
        sheetState = androidx.compose.material3.rememberModalBottomSheetState(skipPartiallyExpanded = true),
        // 面板自己贴到屏幕最底：**不要把导航栏那块让出去**（让出去会在面板下面留一条黑框，用户反馈难看）
        dragHandle = {
            Box(
                Modifier
                    .padding(top = 10.dp, bottom = 6.dp)
                    .size(width = 38.dp, height = 4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.35f))
            )
        }
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                // 面板贴到最底之后，内容自己避开导航栏/手势条（否则内容会被系统按钮压住）
                .navigationBarsPadding()
                .padding(horizontal = 18.dp)
                .padding(bottom = 20.dp)
        ) {
            if (title != null) {
                title()
                Spacer(Modifier.height(8.dp))
            }
            // 按钮放在**顶部**（用户要求）：始终看得见、长表单也不会把「保存」挤走
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically
            ) {
                dismissButton?.invoke()
                Spacer(Modifier.width(8.dp))
                confirmButton()
            }
            Spacer(Modifier.height(12.dp))
            // 内容区：**限高 + 可滚动**（长表单只滚内容，标题与按钮始终在视野里）
            Column(
                Modifier
                    .fillMaxWidth()
                    .heightIn(max = 430.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                text?.invoke()
            }
        }
    }
}

/**
 * App 统一的输入框：**圆角填充**（不再是系统 OutlinedTextField 的直角描边 —— 那正是"跟整体风格不协调"的来源）。
 * 参数形状与 `OutlinedTextField` 兼容，调用点可机械替换。
 */
@Composable
fun AppField(
    value: String,
    onValueChange: (String) -> Unit,
    label: (@Composable () -> Unit)? = null,
    singleLine: Boolean = true,
    minLines: Int = 1,
    maxLines: Int = 1,
    keyboardOptions: androidx.compose.foundation.text.KeyboardOptions = androidx.compose.foundation.text.KeyboardOptions.Default,
    modifier: Modifier = Modifier
) {
    androidx.compose.material3.TextField(
        value = value,
        onValueChange = onValueChange,
        label = label,
        singleLine = singleLine,
        minLines = minLines,
        maxLines = maxLines,
        keyboardOptions = keyboardOptions,
        shape = RoundedCornerShape(12.dp),
        colors = androidx.compose.material3.TextFieldDefaults.colors(
            focusedContainerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.55f),
            unfocusedContainerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.35f),
            focusedIndicatorColor = Color.Transparent,
            unfocusedIndicatorColor = Color.Transparent,
            disabledIndicatorColor = Color.Transparent
        ),
        modifier = modifier.fillMaxWidth()
    )
}


/** 无涟漪点击（HyperOS / 玻璃都不给列表项加涟漪）。 */
fun Modifier.clickableNoRipple(onClick: () -> Unit): Modifier = this.clickable(
    interactionSource = MutableInteractionSource(),
    indication = null,
    onClick = onClick
)

/** 页面标题（每个页顶部一行）。 */
@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        fontSize = 13.sp,
        fontWeight = FontWeight.Medium,
        color = MaterialTheme.colorScheme.onBackground,
        modifier = modifier
    )
}

/** 通用卡片：统一用 MIUIX 卡片（底栏固定液态玻璃，其他界面固定 MIUIX）。 */
@Composable
fun AppCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit
) {
    MiuixCard(modifier = modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), content = content)
    }
}

/** 条目行：左标题 +（可选）副标题，右侧放控件。 */
@Composable
fun SettingRow(
    title: String,
    subtitle: String? = null,
    trailing: (@Composable RowScope.() -> Unit)? = null,
    onClick: (() -> Unit)? = null
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickableNoRipple(onClick) else Modifier)
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 14.sp, color = MaterialTheme.colorScheme.onSurface)
            if (!subtitle.isNullOrBlank()) {
                Text(subtitle, fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2)
            }
        }
        if (trailing != null) {
            Row(verticalAlignment = Alignment.CenterVertically, content = trailing)
        }
    }
}

/** 主按钮（MIUIX 主色）。 */
@Composable
fun AppButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    /**
     * 用于"多选一"（主题色这类）：选中项前面加一个 FontAwesome 对勾。
     * 深色主题下 MIUIX 的主按钮与次要按钮底色很接近，光靠底色分不出哪个是选中的。
     */
    checked: Boolean = false,
    /** 可选 FontAwesome 图标（用户要求：所有界面尽可能用 FA 表达）。 */
    icon: String? = null,
    /** 字号：自动排版按钮行会在放不下时缩小它（用户要求：先缩字号、再裁字）。 */
    fontSize: androidx.compose.ui.unit.TextUnit = 14.sp
) {
    MiuixButton(
        onClick = onClick,
        // 固定高度：选中（实心主色）与未选中（描边）**尺寸必须一致**，否则一行里"选中就变大"很难看（用户反馈）
        modifier = modifier.heightIn(min = 40.dp),
        enabled = enabled,
        colors = MiuixButtonDefaults.buttonColorsPrimary()
    ) {
        // 主按钮是**深色底**：图标必须跟着变成浅色（onPrimary），否则对比度不足看不见
        val tint = MaterialTheme.colorScheme.onPrimary
        if (checked) {
            FaIcon("circle-check", size = 14.dp, tint = tint)
            Spacer(Modifier.width(6.dp))
        }
        if (icon != null) {
            FaIcon(icon, size = 14.dp, tint = tint)
            Spacer(Modifier.width(6.dp))
        }
        androidx.compose.material3.Text(
            text,
            fontSize = fontSize,
            color = MaterialTheme.colorScheme.onPrimary,   // 用户要求：按钮文字用浅色（与图标一致，深底上才清楚）
            maxLines = 1,                      // 用户要求：按钮文字**禁止换行**（窄了宁可裁掉）
            softWrap = false,
            overflow = androidx.compose.ui.text.style.TextOverflow.Clip
        )
    }
}

/** 次要按钮。 */
@Composable
fun AppOutlinedButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    /** 可选 FontAwesome 图标。 */
    icon: String? = null,
    /** 字号：自动排版按钮行会在放不下时缩小它（先缩字号、再裁字）。 */
    fontSize: androidx.compose.ui.unit.TextUnit = 14.sp
) {
    MiuixButton(
        onClick = onClick,
        modifier = modifier.heightIn(min = 40.dp),
        enabled = enabled,
        colors = MiuixButtonDefaults.buttonColors()
    ) {
        if (icon != null) {
            // 次要按钮也有底色：图标要跟着变浅（用 onPrimary），否则深色底 + 深色图标看不见（用户反馈）
            FaIcon(icon, size = 14.dp, tint = MaterialTheme.colorScheme.onSurfaceVariant)   // 未选中：跟随系统明暗（浅色模式深字/暗黑模式浅字）
            Spacer(Modifier.width(6.dp))
        }
        androidx.compose.material3.Text(
            text,
            fontSize = fontSize,
            color = MaterialTheme.colorScheme.onSurfaceVariant,   // 未选中：跟随系统明暗；选中（主色底）才是浅色文字
            maxLines = 1,                      // 用户要求：按钮文字**禁止换行**（窄了宁可裁掉）
            softWrap = false,
            overflow = androidx.compose.ui.text.style.TextOverflow.Clip
        )
    }
}

/** 空状态提示（未连接、无数据时显示）。 */
@Composable
fun EmptyHint(text: String, modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().padding(vertical = 28.dp), contentAlignment = Alignment.Center) {
        Text(
            text,
            fontSize = 13.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

/** 键值对小行（总览页大量使用）。 */
@Composable
fun InfoRow(label: String, value: String) {
    Row(
        Modifier.fillMaxWidth().padding(vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            label,
            fontSize = 12.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.width(88.dp)
        )
        Text(
            value,
            fontSize = 13.sp,
            color = MaterialTheme.colorScheme.onSurface
        )
    }
}

/** 细分隔线。 */
@Composable
fun ThinDivider() {
    Spacer(
        Modifier
            .fillMaxWidth()
            .height(1.dp)
            .background(MaterialTheme.colorScheme.surfaceVariant)
    )
}

/**
 * 可拖动的进度条（自绘，不用 material3 的 Slider）。
 *
 * 为什么自己画：用户反馈原 Slider「不跟手」——它的触摸目标窄、按下要"吸附到拇指"，
 * 而且外面套着竖向滚动，容易和滚动手势打架。这里整条 40dp 高都能按能拖，
 * 按下即定位、移动即跟随，松手才回调（[onFinished]）去下发 seek。
 */
@Composable
fun DragBar(
    fraction: Float,
    onDrag: (Float) -> Unit,
    onFinished: (Float) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    height: Dp = 40.dp
) {
    val accent = MaterialTheme.colorScheme.primary
    val track = MaterialTheme.colorScheme.surfaceVariant
    var width by remember { mutableStateOf(1f) }
    // 记住"最后一次拖到的位置"：以前 onDragEnd 用的是**组合时捕获的** fraction（旧值），
    // 松手时会跳到错误位置（还可能连发好几次 seek）。
    var latest by remember { mutableStateOf(fraction) }
    Box(
        modifier
            .fillMaxWidth()
            .height(height)
            .onSizeChanged { width = it.width.toFloat().coerceAtLeast(1f) }
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                detectTapGestures { off ->
                    val f = (off.x / width).coerceIn(0f, 1f)
                    latest = f
                    onFinished(f)
                }
            }
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                detectHorizontalDragGestures(
                    onDragStart = { off ->
                        val f = (off.x / width).coerceIn(0f, 1f); latest = f; onDrag(f)
                    },
                    onHorizontalDrag = { change, _ ->
                        change.consume()
                        val f = (change.position.x / width).coerceIn(0f, 1f); latest = f; onDrag(f)
                    },
                    onDragEnd = { onFinished(latest) },
                    onDragCancel = { onFinished(latest) }
                )
            },
        contentAlignment = Alignment.CenterStart
    ) {
        // 轨道
        Box(
            Modifier
                .fillMaxWidth()
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(track)
        )
        // 已播放部分
        Box(
            Modifier
                .fillMaxWidth(fraction.coerceIn(0f, 1f))
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(if (enabled) accent else track)
        )
        // 拇指（固定 14dp，位置随 fraction；贴边时也不会跑出容器）
        Box(
            Modifier
                .offset(x = (width * fraction.coerceIn(0f, 1f)).dp / LocalDensity.current.density - 7.dp)
                .size(14.dp)
                .clip(CircleShape)
                .background(if (enabled) accent else track)
        )
    }
}

/**
 * App 统一的**音量条**（用户反馈：Material 自带的 Slider 粗轨道大圆点不美观，与整体风格不搭）。
 * 细轨道 6dp + 小圆点 14dp（外圈浅、内点主色做成环形），支持点击定位与拖动。
 */
@Composable
fun VolumeBar(percent: Int, onChange: (Int) -> Unit, modifier: Modifier = Modifier) {
    val frac = ((percent.coerceIn(20, 200) - 20) / 180f).coerceIn(0f, 1f)
    androidx.compose.foundation.layout.BoxWithConstraints(modifier.fillMaxWidth().height(30.dp)) {
        val thumb = 14.dp
        val x = (maxWidth - thumb) * frac
        Box(
            Modifier
                .fillMaxWidth()
                .pointerInput(Unit) {
                    detectTapGestures { o ->
                        val f = (o.x / size.width.toFloat().coerceAtLeast(1f)).coerceIn(0f, 1f)
                        onChange((20 + f * 180).toInt())
                    }
                }
                .pointerInput(Unit) {
                    detectHorizontalDragGestures { change, _ ->
                        val f = (change.position.x / size.width.toFloat().coerceAtLeast(1f)).coerceIn(0f, 1f)
                        onChange((20 + f * 180).toInt())
                    }
                },
            contentAlignment = Alignment.CenterStart
        ) {
            Box(
                Modifier.fillMaxWidth().height(6.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.85f))
            )
            Box(
                Modifier.fillMaxWidth(frac).height(6.dp)
                    .clip(RoundedCornerShape(3.dp))
                    .background(MaterialTheme.colorScheme.primary)
            )
            Box(
                Modifier.offset(x = x).size(thumb).clip(CircleShape)
                    .background(MaterialTheme.colorScheme.surfaceVariant)
            )
            Box(
                Modifier.offset(x = x + 3.dp).size(thumb - 6.dp).clip(CircleShape)
                    .background(MaterialTheme.colorScheme.primary)
            )
        }
    }
}

/**
 * **等宽选项行**：一行里每个选项按钮的宽度按数量均分（weight），所以**选中前后尺寸完全一致**，
 * 不会出现"选中就变大/变宽"的跳动；每个选项都能带一个 Font Awesome 图标（用户要求）。
 *
 * @param options (值, 显示文字, FA 图标名或 null)
 */
@Composable
fun EqualOptionRow(
    options: List<Triple<String, String, String?>>,
    selected: String,
    onSelect: (String) -> Unit,
    modifier: Modifier = Modifier,
    /** true = 紧凑等宽（宽度只到最宽那项的宽度，不撑满整行；两三个选项时中间不会留大片空白）。 */
    compact: Boolean = false
) {
    // compact：每项**固定宽度**（不用"最宽项决定行宽"—— 选中会多一个对勾图标，那样宽度会跟着变，用户反馈"又大小变了"）
    // compact：宽度**自动**（= 最宽那一项的宽度），两项再均分 → 既自动又等宽
    val rowMod = if (compact) modifier.width(androidx.compose.foundation.layout.IntrinsicSize.Max) else modifier.fillMaxWidth()
    Row(rowMod, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        options.forEach { (value, label, icon) ->
            val on = value == selected
            Box(Modifier.weight(1f)) {
                if (on) {
                    AppButton(label, { onSelect(value) }, checked = false, icon = icon, modifier = Modifier.fillMaxWidth())
                } else {
                    AppOutlinedButton(label, { onSelect(value) }, icon = icon, modifier = Modifier.fillMaxWidth())
                }
            }
        }
    }
}

/** 通用滑条（条状，外观与音量条一致）：fraction 0~1。 */
@Composable
fun AppSlider(fraction: Float, onFraction: (Float) -> Unit, modifier: Modifier = Modifier) {
    val f0 = fraction.coerceIn(0f, 1f)
    androidx.compose.foundation.layout.BoxWithConstraints(modifier.fillMaxWidth().height(30.dp)) {
        val thumb = 14.dp
        val x = (maxWidth - thumb) * f0
        Box(
            Modifier
                .fillMaxWidth()
                .pointerInput(Unit) {
                    detectTapGestures { o ->
                        val f = (o.x / size.width.toFloat().coerceAtLeast(1f)).coerceIn(0f, 1f)
                        onFraction(f)
                    }
                }
                .pointerInput(Unit) {
                    detectHorizontalDragGestures { change, _ ->
                        val f = (change.position.x / size.width.toFloat().coerceAtLeast(1f)).coerceIn(0f, 1f)
                        onFraction(f)
                    }
                },
            contentAlignment = Alignment.CenterStart
        ) {
            Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.85f)))
            Box(Modifier.fillMaxWidth(f0).height(6.dp).clip(RoundedCornerShape(3.dp)).background(MaterialTheme.colorScheme.primary))
            Box(Modifier.offset(x = x).size(thumb).clip(CircleShape).background(MaterialTheme.colorScheme.surfaceVariant))
            Box(Modifier.offset(x = x + 3.dp).size(thumb - 6.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary))
        }
    }
}

/** 一行按钮的规格（给自动排版用）。 */
data class BtnSpec(
    val text: String,
    val icon: String? = null,
    val enabled: Boolean = true,
    val primary: Boolean = false,
    val onClick: () -> Unit
)

/**
 * **自动排版按钮行**（用户要求）：
 *   ① 文字**禁止换行**；② 放不下时**先缩小字号**（14sp → 11sp）；③ 还放不下就**平分整行宽度**（文字裁切）；
 *   ④ 图标始终保留（缩字号不丢图标），内容保持居中。
 * 宽度用 [rememberTextMeasurer] 按真实文字宽度测量，所以是"真放不下才平分"，不会无脑均分。
 */
@Composable
fun AutoFitButtonRow(specs: List<BtnSpec>, modifier: Modifier = Modifier, forceEqual: Boolean = false) {
    if (specs.isEmpty()) return
    val measurer = androidx.compose.ui.text.rememberTextMeasurer()
    val density = androidx.compose.ui.platform.LocalDensity.current
    androidx.compose.foundation.layout.BoxWithConstraints(modifier.fillMaxWidth()) {
        val availPx = constraints.maxWidth.toFloat()
        val gapPx = with(density) { 6.dp.toPx() }
        val iconPx = with(density) { 20.dp.toPx() }
        val padPx = with(density) { 30.dp.toPx() }
        fun needPx(fontSp: Float): Float {
            var sum = 0f
            for (s in specs) {
                val w = measurer.measure(s.text, androidx.compose.ui.text.TextStyle(fontSize = fontSp.sp)).size.width.toFloat()
                sum += maxOf(w, iconPx) + padPx
            }
            return sum + gapPx * (specs.size - 1)
        }
        val fs = if (needPx(14f) <= availPx) 14f else 11f          // ② 先缩字号
        val equal = forceEqual || needPx(fs) > availPx              // ③ 还不行（或调用方要求）→ 平分
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            specs.forEach { s ->
                val m = if (equal) Modifier.weight(1f) else Modifier
                if (s.primary) AppButton(s.text, s.onClick, enabled = s.enabled, icon = s.icon, modifier = m, fontSize = fs.sp)
                else AppOutlinedButton(s.text, s.onClick, enabled = s.enabled, icon = s.icon, modifier = m, fontSize = fs.sp)
            }
        }
    }
}
