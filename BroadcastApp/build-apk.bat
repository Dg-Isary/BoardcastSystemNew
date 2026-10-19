@echo off
rem =====================================================================
rem  DEPRECATED: the old offline route (kotlinc -> d8 -> aapt2 -> apksigner)
rem
rem  Why it can no longer build this app:
rem    The UI is now Jetpack Compose + MIUIX. Plain kotlinc cannot resolve
rem    androidx.compose.* / top.yukonga.miuix.* when the classpath is only
rem    android.jar + zxing.jar, and d8 would additionally have to dex every
rem    dependency AAR (and merge their resources) - that is exactly the job
rem    Gradle + AGP does. The old route only ever produced the pre-Compose
rem    APK, which is why the APK stayed ~1.7 MB and never contained the UI.
rem
rem  This file now simply forwards to build-android.bat (Gradle, the
rem  supported path), so the old command name keeps working.
rem
rem  Usage is unchanged:
rem    build-apk.bat            -> release APK in BroadcastApp\..\dist
rem    build-apk.bat debug      -> debug APK
rem =====================================================================
setlocal
echo [NOTE] build-apk.bat is deprecated - forwarding to build-android.bat
call "%~dp0build-android.bat" %*
exit /b %ERRORLEVEL%
