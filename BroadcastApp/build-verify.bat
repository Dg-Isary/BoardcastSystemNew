@echo off
rem vnt-lite Kotlin reference implementation - offline compile + full verification
rem Uses Android Studio bundled JDK (JBR) and kotlinc; no Gradle / no network needed.
rem Usage: build-verify.bat                  -> offline unit verification only
rem        build-verify.bat 127.0.0.1:29873  -> also run live verification (start vnts first)
setlocal
set "AS=C:\Program Files\Android\Android Studio"
set "JAVA_HOME=%AS%\jbr"
set "KOTLINC=%AS%\plugins\Kotlin\kotlinc\bin\kotlinc.bat"
set "APP=%~dp0"
set "SRC=%APP%protocol\src\main\kotlin"
set "VSRC=%APP%verify\src\main\kotlin"
set "OUT=%APP%out"
if not exist "%OUT%" mkdir "%OUT%"
echo [1/2] compiling kotlin ...
call "%KOTLINC%" "%SRC%" "%VSRC%" -include-runtime -d "%OUT%\app.jar"
if errorlevel 1 ( echo COMPILE FAILED & exit /b 1 )
echo [2/2] running verification %1 ...
"%JAVA_HOME%\bin\java.exe" -jar "%OUT%\app.jar" %1
exit /b %errorlevel%
