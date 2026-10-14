@echo off
rem =====================================================================
rem  Android build (Compose + MIUIX) - one-click APK
rem
rem  Usage:
rem    build-android.bat              release (default, output dist\BroadcastApp.apk)
rem    build-android.bat debug        debug   (output dist\BroadcastApp-debug.apk)
rem =====================================================================
setlocal

set "MODE=%~1"
if "%MODE%"=="" set "MODE=release"

set "APP=%~dp0"
if "%APP:~-1%"=="\" set "APP=%APP:~0,-1%"
set "PROJ=%APP%\android"
for %%I in ("%APP%\..") do set "ROOT=%%~fI"

set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
set "GRADLE_USER_HOME=%ROOT%\.gradle-home"
set "GRADLE_BIN=%APP%\tools\gradle-9.5.0\bin\gradle.bat"

if not exist "%JAVA_HOME%\bin\java.exe" (
  echo [ERROR] JDK not found: %JAVA_HOME%
  echo         Please change JAVA_HOME in this script.
  exit /b 1
)
if not exist "%GRADLE_BIN%" (
  echo [ERROR] Gradle not found: %GRADLE_BIN%
  echo         Please run: copy tools\gradle-9.5.0 from the reference project.
  exit /b 1
)
if not exist "%PROJ%\settings.gradle.kts" (
  echo [ERROR] Project not found: %PROJ%
  exit /b 1
)

if not exist "%GRADLE_USER_HOME%" mkdir "%GRADLE_USER_HOME%"
if not exist "%GRADLE_USER_HOME%\caches\modules-2" (
  echo [HINT] Dependency cache not found: first build will download dependencies.
)

if /I "%MODE%"=="debug" (
  set "TASK=:app:assembleDebug"
  set "OUTNAME=BroadcastApp-debug.apk"
  set "SRC=%PROJ%\app\build\outputs\apk\debug\app-debug.apk"
) else (
  set "TASK=:app:assembleRelease"
  set "OUTNAME=BroadcastApp.apk"
  set "SRC=%PROJ%\app\build\outputs\apk\release\app-release.apk"
)

echo ============================================================
echo  Building Android (%MODE%)
echo    JDK    : %JAVA_HOME%
echo    Gradle : %GRADLE_BIN%
echo    Project: %PROJ%
echo ============================================================

cd /d "%PROJ%"
call "%GRADLE_BIN%" %TASK% --console=plain --no-daemon
set "RC=%ERRORLEVEL%"

rem NOTE: use goto, not "if (...)" blocks, from here on.
rem   A ")" inside an echo line (e.g. "exit code %RC%)") prematurely closes the
rem   parenthesized block; cmd then reports ". was unexpected at this time."
rem   *after* Gradle has already succeeded, silently skipping the copy below.
if not "%RC%"=="0" goto :failed
if not exist "%SRC%" goto :noapk

if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"
copy /y "%SRC%" "%ROOT%\dist\%OUTNAME%" >nul
if errorlevel 1 goto :nocopy

echo.
echo [OK] APK: %ROOT%\dist\%OUTNAME%
for %%F in ("%ROOT%\dist\%OUTNAME%") do echo      Size: %%~zF bytes
echo.
exit /b 0

:failed
echo.
echo [FAILED] Build failed. Gradle exit code %RC%.
exit /b %RC%

:noapk
echo [FAILED] APK not found: %SRC%
exit /b 2

:nocopy
echo [FAILED] Failed to copy APK.
exit /b 3
