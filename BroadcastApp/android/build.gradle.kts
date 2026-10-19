// 智能校园广播系统 · 安卓端（Compose + MiuiX）
//
// 与旧版离线构建（BroadcastApp\build-apk.bat：kotlinc → d8 → aapt2）的关系：
//   · 本 Gradle 工程是**主线**（Compose + MiuiX + 液态玻璃主题）；
//   · 旧的离线流程仍然保留可用（纯 framework 版本），作为"没有网络/没有 Gradle 时的兜底"。
//
// 注意：AGP 9.x 自带 Kotlin 支持，不能再显式应用 org.jetbrains.kotlin.android（会直接报错）；
// 但 Compose 编译器插件仍然必须显式应用。
plugins {
    id("com.android.application") version "9.3.3" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.4.20" apply false
}
