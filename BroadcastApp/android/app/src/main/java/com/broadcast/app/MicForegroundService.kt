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
 * 送话（话筒）期间必须有的**前台服务**，类型 `microphone`。
 *
 * 为什么必须有：
 *   1) Android 12+（API 31）起，App 切到后台后如果**没有** `foregroundServiceType="microphone"`
 *      的前台服务在跑，系统会**直接把麦克风静音** —— 表现就是"息屏/切后台后话筒没声了"。
 *   2) 有前台服务之后，进程不会被系统当作"后台进程"冻结/回收，息屏期间采集与组网都能继续
 *      （用户要求：只要不退出 App，话筒就能用）。
 *   3) MIUI/HyperOS 对后台更激进，这一条尤其重要。
 *
 * 与 MediaProjectionService 的区别：那个是"采集手机系统声音（内录）"，这个只负责"麦克风送话"，
 * 两者互不影响，可以同时存在。
 */
class MicForegroundService : Service() {

    companion object {
        const val CHANNEL = "cbs-mic"
        const val NOTIFY_ID = 1002

        /** 开麦成功后调用（必须由**前台**界面发起：Android 12+ 不允许从后台启动 microphone 前台服务）。 */
        fun start(ctx: Context) {
            try {
                val i = Intent(ctx, MicForegroundService::class.java)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) ctx.startForegroundService(i)
                else ctx.startService(i)
            } catch (_: Throwable) { }
        }

        /** 关麦/退出时调用。 */
        fun stop(ctx: Context) {
            try { ctx.stopService(Intent(ctx, MicForegroundService::class.java)) } catch (_: Throwable) { }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val nm = getSystemService(NotificationManager::class.java)
                if (nm.getNotificationChannel(CHANNEL) == null) {
                    nm.createNotificationChannel(
                        NotificationChannel(CHANNEL, "话筒送话", NotificationManager.IMPORTANCE_LOW)
                    )
                }
            }
            val n: Notification = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                Notification.Builder(this, CHANNEL)
            } else {
                @Suppress("DEPRECATION") Notification.Builder(this)
            }
                .setContentTitle("广播系统手控端")
                .setContentText("话筒使用中：正在向电脑端送话")
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setOngoing(true)
                .build()
            startForeground(NOTIFY_ID, n)
        } catch (_: Throwable) { }
        return START_NOT_STICKY
    }
}
