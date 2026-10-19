plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.broadcast.app"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.broadcast.app"
        minSdk = 26
        // targetSdk 用 36（Android 16 正式版）：顶到预览号只会让新系统按"未来行为"跑，兼容性反而变差
        targetSdk = 36
        versionCode = 40100
        versionName = "4.1"
        // 模拟器是 x86_64、真机是 arm64，两种都带上（这个 App 没有原生库，加着也不增体积）
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }

    // 注意：signingConfigs 必须**排在 buildTypes 之前** ——
    // buildTypes.release 里要读 signingConfigs.findByName("release")，
    // 若本块在后面执行，那时容器里只有默认的 debug，会静默回退成 debug 签名（实测踩过）。
    signingConfigs {
        val ksPath = System.getenv("CBS_KEYSTORE")
        if (!ksPath.isNullOrBlank()) {
            create("release") {
                storeFile = file(ksPath)
                storePassword = System.getenv("CBS_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("CBS_KEY_ALIAS")
                keyPassword = System.getenv("CBS_KEY_PASSWORD")
            }
            println("[签名] 使用发布密钥库: " + ksPath + "  alias=" + System.getenv("CBS_KEY_ALIAS"))
        } else {
            println("[签名] 未提供 CBS_KEYSTORE 等环境变量，release 将回退为 **debug 签名**：" +
                    "不能上架应用商店，且不同机器构建的 APK 无法互相覆盖安装。")
        }
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
            applicationIdSuffix = ".debug"
        }
        release {
            isMinifyEnabled = false          // 先不混淆（协议代码依赖反射少，但稳妥优先）
            // 发布签名：从**环境变量**注入（密钥库与口令绝不写进仓库/脚本）。
            //   CBS_KEYSTORE          密钥库文件路径（.jks / .keystore）
            //   CBS_KEY_ALIAS         别名
            //   CBS_KEYSTORE_PASSWORD 密钥库口令
            //   CBS_KEY_PASSWORD      别名口令（多数情况下与库口令相同）
            // 没有提供时回退到 debug 签名 —— 那样**不能上架应用商店**，
            // 而且不同机器构建出的 APK 签名不同、无法互相覆盖安装（必须卸载重装）。
            // 生成密钥库（一次即可，务必妥善保管，丢了就无法覆盖升级）：
            //   keytool -genkeypair -v -keystore cbs-release.jks -alias cbs \
            //           -keyalg RSA -keysize 2048 -validity 10000
            signingConfig = signingConfigs.findByName("release")
                ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures { compose = true }

    packaging {
        resources { excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    }

    // 协议层源码与旧的离线构建**共用同一份文件**（不在两处复制，避免漂移）
    sourceSets {
        getByName("main") {
            kotlin.srcDir("../../protocol/src/main/kotlin")
        }
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2026.09.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    // 说明：**不再依赖 material-icons-extended** —— 界面图标统一改用 Font Awesome Solid
    //（与电脑端同一份 otf，见 res/font/fa_solid_900.otf 与 ui/Fa.kt），
    // 既满足"图标一律 FontAwesome"的项目规则，也省掉几 MB 的图标库体积。
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")

    // 二维码解码（扫码一键配置，**实时取景**）：
    //   · ML Kit barcode-scanning（**bundled 版**：模型 + native 库都在 AAR 里，离线可用，角度/低光更稳）
    //   · ZXing 作为兜底解码器（个别 ROM 上 ML Kit 不可用时顶上，保证"至少能用"）
    implementation("com.google.mlkit:barcode-scanning:17.3.0")
    implementation("com.google.zxing:core:3.5.3")

    // CameraX：实时预览 + 逐帧分析（要"微信那种对准就中"的体验，必须这样做）
    val cameraX = "1.6.2"
    implementation("androidx.camera:camera-core:$cameraX")
    implementation("androidx.camera:camera-camera2:$cameraX")
    implementation("androidx.camera:camera-lifecycle:$cameraX")
    implementation("androidx.camera:camera-view:$cameraX")

    // MIUIX（小米 HyperOS 设计体系，Apache-2.0）：KMP 版，Android 变体会自动重定向到 androidx.compose 组件
    implementation("top.yukonga.miuix.kmp:miuix:0.8.8")
    // 说明：不要去依赖 miuix-blur —— 它在 Maven 上只有 KMP 元数据与 -android 变体，
    // 而 -android 不是可直接依赖的坐标（实测 "Could not find ...miuix-blur:0.8.8"）。
    // 液态玻璃/毛玻璃效果我们自己画（Compose 的 graphicsLayer + RenderEffect 模糊 + 高光描边）。

    debugImplementation("androidx.compose.ui:ui-tooling")
}
