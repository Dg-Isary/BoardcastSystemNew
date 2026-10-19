package com.broadcast.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder

/**
 * 采集系统声音时必须有的**前台服务**。
 *
 * 为什么必须有：Android 14（API 34）起，调用 `MediaProjectionManager.getMediaProjection()`
 * 之前必须先有一个 `foregroundServiceType="mediaProjection"` 的前台服务在跑，否则系统直接抛异常；
 * 低版本也建议这么做（否则 App 切到后台就可能被限制采集）。这里的服务**只做这一件事**：
 * 挂一条常驻通知，让系统知道"正在采集音频"，不录屏、不碰画面。
 */
class MediaProjectionService : Service() {

    companion object {
        const val CHANNEL = "cbs-capture"
        const val NOTIFY_ID = 1001
        fun start(ctx: Context) {
            try {
                val i = Intent(ctx, MediaProjectionService::class.java)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) ctx.startForegroundService(i)
                else ctx.startService(i)
            } catch (_: Throwable) { }
        }
        fun stop(ctx: Context) {
            try { ctx.stopService(Intent(ctx, MediaProjectionService::class.java)) } catch (_: Throwable) { }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val nm = getSystemService(NotificationManager::class.java)
                if (nm.getNotificationChannel(CHANNEL) == null) {
                    nm.createNotificationChannel(
                        NotificationChannel(CHANNEL, "采集系统声音", NotificationManager.IMPORTANCE_LOW)
                    )
                }
            }
            val n: Notification = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                Notification.Builder(this, CHANNEL)
            } else {
                @Suppress("DEPRECATION") Notification.Builder(this)
            }
                .setContentTitle("广播系统手控端")
                .setContentText("正在采集手机声音（用于喊话），不录制屏幕")
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setOngoing(true)
                .build()
            startForeground(NOTIFY_ID, n)
        } catch (_: Throwable) { }
        return START_NOT_STICKY
    }
}
