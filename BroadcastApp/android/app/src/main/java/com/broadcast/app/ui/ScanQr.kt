package com.broadcast.app.ui

import android.Manifest
import android.content.pm.PackageManager
import android.util.Size
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.broadcast.app.core.ScanConfigParser
import com.broadcast.app.core.ScannedConfig
import com.google.mlkit.vision.barcode.BarcodeScanner
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.util.concurrent.Executors

/**
 * 扫码配置页：**实时取景 + 连续识别**（用户要求：做到微信那种体验，对准就中）。
 *
 * 识别链路：
 *   ① 首选 **ML Kit `barcode-scanning`（bundled 版）** —— 模型随包，离线可用，角度/低光更稳；
 *   ② 若 ML Kit 在该机型上不可用（初始化失败/一直不出结果），自动回退到 **ZXing** 解同一帧的 Y 平面，
 *      保证"至少能用"，不会因为某个 ROM 的坑变成一块黑屏。
 *
 * 其它：
 *   · 相机权限被拒、或机型没有后置相机时，提供**手动粘贴二维码内容**兜底；
 *   · 界面里给取景框 + 提示，找到码立刻回调，不再需要"拍一张再看"。
 */
@Composable
fun ScanQrScreen(
    onScanned: (ScannedConfig, String) -> Unit,
    onCancel: () -> Unit,
    /** 从相册选图并解码（由 MainActivity 注入；null = 这个界面拿不到相册入口）。 */
    galleryPicker: (((String?) -> Unit) -> Unit)? = null
) {
    val context = LocalContext.current
    var hasPermission by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        )
    }
    var error by remember { mutableStateOf("") }
    var found by remember { mutableStateOf(false) }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        hasPermission = granted
        if (!granted) {
            error = "相机权限被拒：可以点下面的相册按钮，选电脑端二维码的截图来扫。"
        }
    }
    LaunchedEffect(Unit) { if (!hasPermission) permissionLauncher.launch(Manifest.permission.CAMERA) }

    fun accept(text: String) {
        if (found) return
        val raw = text.trim()
        if (raw.isEmpty()) return
        val cfg = ScanConfigParser.parse(raw) { error = it }
        if (cfg != null) {
            found = true
            onScanned(cfg, raw)
        }
    }

    Column(Modifier.fillMaxSize()) {
        // ---- 取景区（占满剩余空间）----
        Box(Modifier.weight(1f).fillMaxWidth().background(Color.Black)) {
            if (hasPermission) {
                CameraPreview(
                    modifier = Modifier.fillMaxSize(),
                    onQr = { text -> accept(text) }
                )
                ScanFrame()
                Text(
                    "把电脑端「vnt 设置」窗里的二维码放进框里，对准就自动识别",
                    fontSize = 12.sp,
                    color = Color.White.copy(alpha = 0.85f),
                    modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 18.dp)
                )
            } else {
                Column(
                    Modifier.fillMaxSize().padding(24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    FaIcon("camera", size = 44.dp, tint = MaterialTheme.colorScheme.primary)
                    Spacer(Modifier.height(16.dp))
                    Text(
                        if (hasPermission) "对准二维码即可" else "需要相机权限才能实时扫码，或用下面的相册按钮",
                        fontSize = 15.sp,
                        color = Color.White
                    )
                }
            }
        }

        // ---- 底部操作区 ----
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            if (error.isNotBlank()) {
                Text(error, fontSize = 12.sp, color = MaterialTheme.colorScheme.error)
                Spacer(Modifier.height(8.dp))
            }
            // 用户要求：去掉「手动粘贴」「取消」两个文字按钮，只留一个**相册扫码**图标按钮
            // （电脑端二维码可以直接截图存相册，扫屏失败时用它；图标不写字）
            Row(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically
            ) {
                // 图标按钮（不写字）：点它从相册选二维码截图
                Box(
                    Modifier
                        .size(48.dp)
                        .clip(RoundedCornerShape(24.dp))
                        .background(MaterialTheme.colorScheme.primary)
                        .clickableNoRipple {
                            val picker = galleryPicker
                            if (picker == null) {
                                error = "当前界面不支持相册扫码，请用相机直接扫"
                            } else {
                                picker { text ->
                                    if (text.isNullOrBlank()) error = "这张图里没认出二维码：换一张更清晰的截图再试"
                                    else accept(text)
                                }
                            }
                        },
                    contentAlignment = Alignment.Center
                ) {
                    FaIcon("image", size = 20.dp, tint = MaterialTheme.colorScheme.onPrimary)
                }
            }
            Spacer(Modifier.height(6.dp))
            Text(
                "提示：配置扫一次就记住了，下次打开 App 会自动组网，不用再扫。",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

/** 取景框（四角描边，中间透明），纯 Compose 画，不依赖相机库。 */
@Composable
private fun ScanFrame() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Box(
            Modifier
                .size(240.dp)
                .border(2.dp, Color.White.copy(alpha = 0.9f), RoundedCornerShape(18.dp))
        )
    }
}

/**
 * CameraX 预览 + 逐帧识别。
 *
 * 说明：Compose 里没有官方的 CameraX 预览组件，所以用 [AndroidView] 包一个 [PreviewView]
 * （这是 CameraX 官方推荐用法），分析帧走 [ImageAnalysis]。
 */
@Composable
private fun CameraPreview(modifier: Modifier, onQr: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val previewView = remember {
        PreviewView(context).apply { scaleType = PreviewView.ScaleType.FILL_CENTER }
    }
    val executor = remember { Executors.newSingleThreadExecutor() }
    // ML Kit 是否可用（不可用就只用 ZXing；见文件头说明）
    val mlKitBroken = remember { booleanArrayOf(false) }
    val scanner: BarcodeScanner? = remember {
        try {
            BarcodeScanning.getClient(
                BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
            )
        } catch (t: Throwable) {
            mlKitBroken[0] = true
            null
        }
    }

    DisposableEffect(Unit) {
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            try {
                val provider = future.get()
                val preview = Preview.Builder().build().also {
                    it.setSurfaceProvider(previewView.surfaceProvider)
                }
                val analysis = ImageAnalysis.Builder()
                    .setTargetResolution(Size(1280, 720))
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()
                analysis.setAnalyzer(executor) { proxy ->
                    analyzeFrame(proxy, scanner, mlKitBroken, onQr)
                }
                provider.unbindAll()
                provider.bindToLifecycle(
                    lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis
                )
            } catch (t: Throwable) {
                android.util.Log.w("BroadcastApp", "相机启动失败（可改用相册扫码）", t)
            }
        }, ContextCompat.getMainExecutor(context))

        onDispose {
            try { future.get().unbindAll() } catch (_: Throwable) { }
            try { scanner?.close() } catch (_: Throwable) { }
            executor.shutdown()
        }
    }

    AndroidView(factory = { previewView }, modifier = modifier)
}

/** 逐帧识别：ML Kit 优先，失败/不可用则用 ZXing 解同一帧。 */
private fun analyzeFrame(
    proxy: ImageProxy,
    scanner: BarcodeScanner?,
    mlKitBroken: BooleanArray,
    onQr: (String) -> Unit
) {
    try {
        val media = proxy.image
        if (media == null) return
        if (scanner != null && !mlKitBroken[0]) {
            val image = InputImage.fromMediaImage(media, proxy.imageInfo.rotationDegrees)
            scanner.process(image)
                .addOnSuccessListener { codes ->
                    codes.firstOrNull { !it.rawValue.isNullOrBlank() }?.rawValue?.let { text ->
                        onQr(text)
                    }
                }
                .addOnFailureListener { t ->
                    // 该机型上 ML Kit 用不了：以后都走 ZXing，避免每帧都失败
                    mlKitBroken[0] = true
                    android.util.Log.w("BroadcastApp", "ML Kit 条码识别失败，改用 ZXing", t)
                    decodeWithZxing(proxy)?.let { onQr(it) }
                }
                .addOnCompleteListener { proxy.close() }
        } else {
            decodeWithZxing(proxy)?.let { onQr(it) }
            proxy.close()
        }
    } catch (t: Throwable) {
        try { proxy.close() } catch (_: Throwable) { }
    }
}

/**
 * 从相册图片里解二维码（用户要求：扫码配置支持"从相册选图"）。
 *
 * 电脑端二维码可以直接截图存相册，扫屏反光/距离不合适时用它更快。
 * 用相册选择器（PickVisualMedia）**不需要存储权限**；解码走 ZXing（与相机那条路径同一个库）。
 */
/**
 * 相册扫码（异步版）：**先用 ML Kit，再用 ZXing 多尺度兜底**。
 *
 * 为什么之前"识别不到二维码"：只用了 ZXing 单次解码，屏幕截图常有缩放/低对比/浅色二维码，
 * ZXing 容易失败；ML Kit 对这类图更稳（相机那条路径本来就是 ML Kit 优先）。
 */
fun decodeQrFromUriAsync(context: android.content.Context, uri: android.net.Uri, onResult: (String?) -> Unit) {
    val bmp: android.graphics.Bitmap = try {
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            val src = android.graphics.ImageDecoder.createSource(context.contentResolver, uri)
            android.graphics.ImageDecoder.decodeBitmap(src) { d, _, _ ->
                d.allocator = android.graphics.ImageDecoder.ALLOCATOR_SOFTWARE
                d.isMutableRequired = false
            }
        } else {
            @Suppress("DEPRECATION")
            android.provider.MediaStore.Images.Media.getBitmap(context.contentResolver, uri)
        }
    } catch (t: Throwable) {
        onResult(null); return
    }
    fun zxing(): String? = try {
        val w = bmp.width; val h = bmp.height
        val base = if (maxOf(w, h) > 1600) 1600f / maxOf(w, h) else 1f
        // 多尺度 + 放大一点再试：二维码在整屏截图里往往只占一小块，缩小后可能就糊了
        val scales = listOf(base, base * 1.5f, base * 2.2f)
        var hit: String? = null
        for (s in scales) {
            val sw = maxOf(1, (w * s).toInt()); val sh = maxOf(1, (h * s).toInt())
            if (sw > 6000 || sh > 6000) continue
            val b = if (s == 1f) bmp else android.graphics.Bitmap.createScaledBitmap(bmp, sw, sh, true)
            val px = IntArray(sw * sh)
            b.getPixels(px, 0, sw, 0, 0, sw, sh)
            val reader = MultiFormatReader().apply {
                setHints(mapOf(
                    DecodeHintType.TRY_HARDER to true,
                    DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE)
                ))
            }
            hit = try { reader.decode(BinaryBitmap(HybridBinarizer(RGBLuminanceSource(sw, sh, px)))).text } catch (t: Throwable) { null }
            if (!hit.isNullOrBlank()) break
        }
        hit
    } catch (t: Throwable) { null }

    try {
        val img = InputImage.fromBitmap(bmp, 0)
        com.google.mlkit.vision.barcode.BarcodeScanning.getClient().process(img)
            .addOnSuccessListener { list ->
                val v = list.firstOrNull()?.rawValue
                onResult(if (!v.isNullOrBlank()) v else zxing())
            }
            .addOnFailureListener { onResult(zxing()) }
    } catch (t: Throwable) {
        onResult(zxing())
    }
}

fun decodeQrFromUri(context: android.content.Context, uri: android.net.Uri): String? {
    val bmp: android.graphics.Bitmap = try {
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            val src = android.graphics.ImageDecoder.createSource(context.contentResolver, uri)
            android.graphics.ImageDecoder.decodeBitmap(src) { d, _, _ ->
                d.allocator = android.graphics.ImageDecoder.ALLOCATOR_SOFTWARE
                d.isMutableRequired = false
            }
        } else {
            @Suppress("DEPRECATION")
            android.provider.MediaStore.Images.Media.getBitmap(context.contentResolver, uri)
        }
    } catch (t: Throwable) {
        return null
    }
    return try {
        val w = bmp.width; val h = bmp.height
        // 图太大先等比缩到 1600 像素以内：既快，ZXing 也更容易二值化成功
        val scale = if (maxOf(w, h) > 1600) 1600f / maxOf(w, h) else 1f
        val sw = maxOf(1, (w * scale).toInt()); val sh = maxOf(1, (h * scale).toInt())
        val small = if (scale == 1f) bmp else android.graphics.Bitmap.createScaledBitmap(bmp, sw, sh, true)
        val px = IntArray(sw * sh)
        small.getPixels(px, 0, sw, 0, 0, sw, sh)
        val reader = MultiFormatReader().apply {
            setHints(
                mapOf(
                    DecodeHintType.TRY_HARDER to true,
                    DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE)
                )
            )
        }
        reader.decode(BinaryBitmap(HybridBinarizer(RGBLuminanceSource(sw, sh, px)))).text
    } catch (t: Throwable) {
        null
    }
}

/** ZXing 解 Y 平面（灰度），作为 ML Kit 的兜底。 */
private fun decodeWithZxing(proxy: ImageProxy): String? {
    return try {
        val plane = proxy.planes.firstOrNull() ?: return null
        val buffer = plane.buffer
        val data = ByteArray(buffer.remaining())
        buffer.get(data)
        val source = PlanarYUVLuminanceSource(
            data, plane.rowStride, proxy.height, 0, 0, proxy.width, proxy.height, false
        )
        val reader = MultiFormatReader().apply {
            setHints(mapOf(DecodeHintType.TRY_HARDER to false))
        }
        reader.decode(BinaryBitmap(HybridBinarizer(source))).text
    } catch (t: Throwable) {
        null
    }
}

/**
 * 相册扫码（**带分步日志**版）：识别失败时能一眼看出是哪一步不行。
 *
 * 步骤：读图（BitmapFactory → ImageDecoder）→ ML Kit → ZXing 多尺度/中央裁剪放大。
 * 每一步都通过 [log] 写进 App 的操作日志（导出日志时会带上），用于定位"相册扫不到二维码"。
 */
fun decodeQrFromUriVerbose(
    context: android.content.Context,
    uri: android.net.Uri,
    log: (String) -> Unit = {},
    onResult: (String?) -> Unit
) {
    log("相册扫码：收到图片 ${uri.scheme}://${uri.authority}")
    var b0: android.graphics.Bitmap? = null
    try {
        context.contentResolver.openInputStream(uri)?.use { ins ->
            b0 = android.graphics.BitmapFactory.decodeStream(ins)
        }
        if (b0 != null) log("相册扫码：BitmapFactory 读取成功 ${b0!!.width}×${b0!!.height}")
        else log("相册扫码：BitmapFactory 返回空，改用 ImageDecoder")
    } catch (t: Throwable) {
        log("相册扫码：BitmapFactory 失败 ${t.javaClass.simpleName}: ${t.message}")
    }
    if (b0 == null && android.os.Build.VERSION.SDK_INT >= 28) {
        try {
            val src = android.graphics.ImageDecoder.createSource(context.contentResolver, uri)
            b0 = android.graphics.ImageDecoder.decodeBitmap(src) { d, _, _ ->
                d.allocator = android.graphics.ImageDecoder.ALLOCATOR_SOFTWARE
                d.isMutableRequired = false
            }
            log("相册扫码：ImageDecoder 读取成功 ${b0!!.width}×${b0!!.height}")
        } catch (t: Throwable) {
            log("相册扫码：ImageDecoder 失败 ${t.javaClass.simpleName}: ${t.message}")
        }
    }
    val src0 = b0
    if (src0 == null) { log("相册扫码：这张图打不开（云端图片/格式不支持/没有读取权限）"); onResult(null); return }

    fun zx(b: android.graphics.Bitmap, tag: String): String? = try {
        val w = b.width; val h = b.height
        val px = IntArray(w * h)
        b.getPixels(px, 0, w, 0, 0, w, h)
        val reader = MultiFormatReader().apply {
            setHints(
                mapOf(
                    DecodeHintType.TRY_HARDER to true,
                    DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE)
                )
            )
        }
        reader.decode(BinaryBitmap(HybridBinarizer(RGBLuminanceSource(w, h, px)))).text?.ifBlank { null }
    } catch (t: Throwable) {
        log("相册扫码：$tag ZXing 未识别（${t.javaClass.simpleName}）")
        null
    }

    fun resize(b: android.graphics.Bitmap, f: Float): android.graphics.Bitmap {
        val w = maxOf(1, (b.width * f).toInt()); val h = maxOf(1, (b.height * f).toInt())
        return if (w == b.width && h == b.height) b else android.graphics.Bitmap.createScaledBitmap(b, w, h, true)
    }

    fun crop(b: android.graphics.Bitmap, f: Float): android.graphics.Bitmap {
        val w = maxOf(1, (b.width * f).toInt()); val h = maxOf(1, (b.height * f).toInt())
        return android.graphics.Bitmap.createBitmap(b, (b.width - w) / 2, (b.height - h) / 2, w, h)
    }

    fun fallback(tag: String): String? {
        val tries = listOf(
            "原图" to src0,
            "放大1.5倍" to resize(src0, 1.5f),
            "放大2.2倍" to resize(src0, 2.2f),
            "中央80%放大2倍" to resize(crop(src0, 0.8f), 2f),
            "中央50%放大3倍" to resize(crop(src0, 0.5f), 3f)
        )
        for ((name, b) in tries) {
            val v = zx(b, "$tag/$name")
            if (!v.isNullOrBlank()) { log("相册扫码：$tag 用「$name」识别成功（${v.length} 字符）"); return v }
        }
        return null
    }

    try {
        val img = InputImage.fromBitmap(src0, 0)
        com.google.mlkit.vision.barcode.BarcodeScanning.getClient().process(img)
            .addOnSuccessListener { list ->
                val withText = list.filter { !it.rawValue.isNullOrBlank() }
                log("相册扫码：ML Kit 返回 ${list.size} 个条码，其中有内容 ${withText.size} 个"
                    + if (list.isNotEmpty()) "（格式 " + list.joinToString(",") { it.format.toString() } + "）" else "")
                val v = withText.firstOrNull()?.rawValue
                if (!v.isNullOrBlank()) { log("相册扫码：ML Kit 识别成功（${v.length} 字符）"); onResult(v) }
                else {
                    val z = fallback("ZXing")
                    if (z.isNullOrBlank()) log("相册扫码：全部方式都没认出二维码（图片 ${src0.width}×${src0.height}）")
                    onResult(z)
                }
            }
            .addOnFailureListener { e ->
                log("相册扫码：ML Kit 失败 ${e.javaClass.simpleName}: ${e.message}")
                onResult(fallback("ZXing"))
            }
    } catch (t: Throwable) {
        log("相册扫码：ML Kit 异常 ${t.javaClass.simpleName}: ${t.message}")
        onResult(fallback("ZXing"))
    }
}