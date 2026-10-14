package com.broadcast.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.BlurredEdgeTreatment
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * 液态玻璃的实现要点（不依赖任何三方库，纯 Compose）：
 *
 *  1. **背景内容**：先铺一层彩色渐变壁纸（[GlassBackdrop]）。模糊必须有东西可糊，
 *     纯灰底糊出来是一团平灰，看不出玻璃感。
 *  2. **模糊只作用于背景层**：`Modifier.blur()` 会连**子树内容一起糊掉**（第一版就踩了这个坑：
 *     卡片里的文字也被糊成一团）。所以这里把结构拆成"背景层（模糊 + 半透明）+ 内容层（不模糊）"，
 *     二者叠在同一个 `Box` 里。
 *  3. **高光描边**用 `drawBehind`：画在背景之上、内容之下。
 *  4. Android 12+ 走 RenderEffect 实时模糊；低版本自动退化为无模糊，靠半透明 + 描边仍有层次。
 */
@Composable
fun GlassBackdrop(content: @Composable BoxScope.() -> Unit) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(
                Brush.verticalGradient(
                    colors = listOf(GlassBackdropTop, GlassBackdropMid, GlassBackdropBottom)
                )
            ),
        content = content
    )
}

/**
 * 玻璃表面：背景模糊 + 半透明 + 高光描边（内容不模糊）。
 *
 * @param alpha 底色不透明度（越低碳越"透"）
 * @param blurRadius 背景模糊半径
 * @param strokeAlpha 描边强度
 * @param cornerRadiusPx 圆角像素半径（描边要与 shape 的圆角一致才贴合）
 */
@Composable
fun GlassSurface(
    modifier: Modifier = Modifier,
    shape: Shape = RoundedCornerShape(24.dp),
    alpha: Float = 0.42f,
    blurRadius: Dp = 28.dp,
    strokeAlpha: Float = 0.28f,
    cornerRadiusPx: Float = 24f,
    tint: Color? = null,
    content: @Composable BoxScope.() -> Unit
) {
    val base = tint ?: MaterialTheme.colorScheme.surface
    Box(modifier = modifier.clip(shape)) {
        // 背景层：模糊 + 半透明 + 描边（与内容层分离，避免把文字一起糊掉）
        Box(
            modifier = Modifier
                .matchParentSize()
                .blur(blurRadius, edgeTreatment = BlurredEdgeTreatment.Unbounded)
                .background(base.copy(alpha = alpha))
                .drawBehind {
                    val r = CornerRadius(cornerRadiusPx * density, cornerRadiusPx * density)
                    // 左上角高光反射
                    drawRect(
                        brush = Brush.linearGradient(
                            colors = listOf(Color.White.copy(alpha = strokeAlpha * 0.55f), Color.Transparent),
                            start = Offset(0f, 0f),
                            end = Offset(size.width * 0.55f, size.height * 0.75f)
                        )
                    )
                    // 高光描边：上亮下淡，模拟玻璃边缘反光
                    drawRoundRect(
                        brush = Brush.verticalGradient(
                            colors = listOf(
                                Color.White.copy(alpha = (strokeAlpha * 2.2f).coerceAtMost(1f)),
                                Color.White.copy(alpha = strokeAlpha * 0.4f)
                            )
                        ),
                        cornerRadius = r,
                        style = Stroke(width = 1.2f * density)
                    )
                }
        )
        // 内容层：正常绘制，不模糊
        content()
    }
}

/** 玻璃卡片（页面里最常用的容器）。 */
@Composable
fun GlassCard(
    modifier: Modifier = Modifier,
    alpha: Float = 0.42f,
    blurRadius: Dp = 28.dp,
    cornerRadiusPx: Float = 24f,
    content: @Composable BoxScope.() -> Unit
) {
    GlassSurface(
        modifier = modifier,
        alpha = alpha,
        blurRadius = blurRadius,
        cornerRadiusPx = cornerRadiusPx,
        content = content
    )
}
