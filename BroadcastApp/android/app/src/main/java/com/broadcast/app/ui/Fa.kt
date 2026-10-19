package com.broadcast.app.ui

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.broadcast.app.R

/**
 * Font Awesome 图标（字体方式）。
 *
 * 字体文件 `res/font/fa_solid_900.otf` 是 Font Awesome Free 的 Solid 字体，
 * 字形码位由 `tools/make_icons.py` 从官方 `all.css` 里解析出来，生成到 `FaIcons.kt`。
 * 用法：`FaIcon("camera")` / `BtnContent("user-plus", "录入")`。
 *
 * 许可：Font Awesome Free，图标 CC BY 4.0、字体 SIL OFL 1.1、代码 MIT；
 * 许可证全文随包放在 `assets/FontAwesome-LICENSE.txt`（再分发必须带上）。
 */
val FaSolid: FontFamily = FontFamily(Font(R.font.fa_solid_900))

/**
 * 画一个 Font Awesome 图标。
 *
 * @param name 图标名，例如 "camera"、"clipboard-check"（见 FaIcons 或 Font Awesome 官网）
 * @param size 字号；FA 字形按 1em 设计，所以 size ≈ 图标视觉边长
 */
@Composable
fun FaIcon(
    name: String,
    modifier: Modifier = Modifier,
    size: Dp = 18.dp,
    tint: Color = LocalContentColor.current
) {
    Text(
        text = FaIcons.glyph(name),
        fontFamily = FaSolid,
        fontSize = size.value.sp,
        color = tint,
        maxLines = 1,
        modifier = modifier
    )
}

/** 按钮/列表项里“图标 + 文字”的标准写法，避免各处自己拼间距。 */
@Composable
fun BtnContent(icon: String?, text: String, size: Dp = 16.dp) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        if (icon != null) {
            FaIcon(icon, size = size)
            Spacer(Modifier.width(6.dp))
        }
        Text(
            text,
            maxLines = 1,
            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
        )
    }
}

/** 纯图标（用于 NavigationBarItem 这类只要图标的场合）。 */
@Composable
fun IconOnly(name: String, size: Dp = 22.dp) {
    // 不套 Modifier.size: 系统字号放大时 sp > dp, 固定尺寸会把图标裁掉
    FaIcon(name, size = size)
}
