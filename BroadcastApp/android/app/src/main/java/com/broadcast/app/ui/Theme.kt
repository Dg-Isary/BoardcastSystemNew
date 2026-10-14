package com.broadcast.app.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.remember
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import top.yukonga.miuix.kmp.theme.Colors
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.theme.darkColorScheme as miuixDark
import top.yukonga.miuix.kmp.theme.lightColorScheme as miuixLight

/** 应用版本号 —— 与 app/build.gradle.kts 的 versionName 保持一致。 */
const val APP_VERSION = "4.0"

/** 构建标记（每次出包手动更新，格式 月日-时分）：手机上装的是哪一次构建，一眼能对上。 */
const val APP_BUILD = "1012-1700"

/**
 * 界面皮肤：
 *  · MIUIX —— 小米 HyperOS 设计体系（实心卡片、系统级配色、连续曲率圆角）；
 *  · Glass —— 液态玻璃（半透明 + 背景模糊 + 高光描边 + 玻璃渐变）。
 * 两套共用同一份色板，切换只影响"材质"，不影响配色与布局。
 */
enum class AppSkin(val key: String, val label: String) {
    Miuix("miuix", "MIUIX"),
    Glass("glass", "液态玻璃");

    companion object {
        fun from(key: String?): AppSkin = entries.firstOrNull { it.key == key } ?: Miuix
    }
}

/** 一套皮肤的外观参数：卡片/导航/输入框的底色、模糊强度、描边、透明度都由它统一定义。 */
data class SkinSpec(
    val skin: AppSkin,
    val cardAlpha: Float,
    val barAlpha: Float,
    val blurRadius: Dp,
    val strokeAlpha: Float,
    val corner: Dp,
    val highlightAlpha: Float
)

val SkinMiuix = SkinSpec(
    skin = AppSkin.Miuix,
    cardAlpha = 1f,
    barAlpha = 1f,
    blurRadius = 0.dp,
    strokeAlpha = 0f,
    corner = 16.dp,
    highlightAlpha = 0f
)

val SkinGlass = SkinSpec(
    skin = AppSkin.Glass,
    cardAlpha = 0.42f,
    barAlpha = 0.52f,
    blurRadius = 28.dp,
    strokeAlpha = 0.28f,
    corner = 24.dp,
    highlightAlpha = 0.18f
)

/** 当前皮肤（供各组件读取；写在外面是为了让任何层级的 Composable 都能拿到）。 */
val LocalSkinSpec = staticCompositionLocalOf { SkinMiuix }

/** 液态玻璃的壁纸底色：模糊需要有内容可模糊，纯灰底会糊成一片。 */
val GlassBackdropTop = Color(0xFF2B4C7E)
val GlassBackdropMid = Color(0xFF3F7EA6)
val GlassBackdropBottom = Color(0xFF6EC6C0)

/** 主题色预设（沿用 HyperOS 那套"以主色为中心生成整块色板"的做法）。 */
data class ThemePreset(val key: String, val label: String, val color: Color)

val THEME_PRESETS = listOf(
    ThemePreset("blue", "极光蓝", Color(0xFF3388FF)),
    ThemePreset("cyan", "青碧", Color(0xFF00B0A6)),
    ThemePreset("green", "森绿", Color(0xFF2AA45C)),
    ThemePreset("violet", "紫罗兰", Color(0xFF7C5CFF)),
    ThemePreset("amber", "琥珀", Color(0xFFF08A00)),
    ThemePreset("rose", "玫瑰", Color(0xFFEE4B6A))
)

fun themePresetColor(key: String): Color =
    THEME_PRESETS.firstOrNull { it.key == key }?.color ?: THEME_PRESETS.first().color

private fun brandLight(brand: Color): Colors = miuixLight(
    primary = brand,
    onPrimary = Color.White,
    primaryVariant = lerp(brand, Color.Black, 0.16f),
    onPrimaryVariant = Color.White,
    primaryContainer = lerp(brand, Color.White, 0.86f),
    onPrimaryContainer = lerp(brand, Color.Black, 0.30f),
    disabledPrimary = lerp(brand, Color.White, 0.62f),
    disabledPrimaryButton = lerp(brand, Color.White, 0.55f),
    disabledPrimarySlider = lerp(brand, Color.White, 0.62f),
    secondary = brand,
    onSecondary = Color.White,
    secondaryVariant = lerp(brand, Color.Black, 0.16f),
    onSecondaryVariant = Color.White
)

private fun brandDark(brand: Color): Colors = miuixDark(
    primary = lerp(brand, Color.White, 0.12f),
    onPrimary = Color.White,
    primaryVariant = lerp(brand, Color.White, 0.30f),
    onPrimaryVariant = lerp(brand, Color.Black, 0.70f),
    primaryContainer = lerp(brand, Color.Black, 0.62f),
    onPrimaryContainer = lerp(brand, Color.White, 0.72f),
    disabledPrimary = lerp(brand, Color.Black, 0.55f),
    disabledPrimaryButton = lerp(brand, Color.Black, 0.50f),
    disabledPrimarySlider = lerp(brand, Color.Black, 0.55f),
    secondary = lerp(brand, Color.White, 0.12f),
    onSecondary = Color.White,
    secondaryVariant = lerp(brand, Color.White, 0.30f),
    onSecondaryVariant = lerp(brand, Color.Black, 0.70f)
)

/**
 * 应用主题：以 MIUIX 为底，再把它的色值搬进 MaterialTheme，避免两套色系打架。
 *
 * 说明（用户要求）：**不再有皮肤切换** —— 内容区固定 MIUIX，底部导航栏固定液态玻璃
 * （玻璃材质在 `App.kt` 的 `GlassDock` 里自绘）。所以这里也不再需要 `AppSkin` / `SkinSpec`。
 *
 * @param themeColor 主题色 key（见 [THEME_PRESETS]）
 */
@Composable
fun BroadcastTheme(
    themeColor: String = "blue",
    content: @Composable () -> Unit
) {
    val dark = isSystemInDarkTheme()
    val brand = themePresetColor(themeColor)
    val scheme = remember(dark, brand) { if (dark) brandDark(brand) else brandLight(brand) }
    MiuixTheme(colors = scheme) {
        val m = MiuixTheme.colorScheme
        MaterialTheme(
            colorScheme = if (dark) darkColorScheme(
                primary = m.primary, onPrimary = m.onPrimary,
                primaryContainer = m.primaryContainer, onPrimaryContainer = m.onPrimaryContainer,
                secondary = m.secondary, onSecondary = m.onSecondary,
                tertiary = m.tertiaryContainer,
                background = m.background, onBackground = m.onBackground,
                surface = m.surface, onSurface = m.onSurface,
                surfaceVariant = m.secondaryContainer, onSurfaceVariant = m.onSurfaceVariantSummary,
                error = m.error, onError = m.onError,
                outline = m.onBackgroundVariant, outlineVariant = m.surfaceVariant
            ) else lightColorScheme(
                primary = m.primary, onPrimary = m.onPrimary,
                primaryContainer = m.primaryContainer, onPrimaryContainer = m.onPrimaryContainer,
                secondary = m.secondary, onSecondary = m.onSecondary,
                tertiary = m.tertiaryContainer,
                background = m.background, onBackground = m.onBackground,
                surface = m.surface, onSurface = m.onSurface,
                surfaceVariant = m.secondaryContainer, onSurfaceVariant = m.onSurfaceVariantSummary,
                error = m.error, onError = m.onError,
                outline = m.onBackgroundVariant, outlineVariant = m.surfaceVariant
            ),
            content = content
        )
    }
}
