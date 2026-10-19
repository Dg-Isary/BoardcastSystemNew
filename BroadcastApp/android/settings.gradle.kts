pluginManagement {
    repositories {
        // 【顺序很重要】官方仓库必须排在镜像前面：
        // Gradle 只在 404 时回退到下一个仓库；遇到 502/503 这类服务端错误会**直接失败**。
        // GitHub Actions 的 runner 在境外，阿里云镜像偶发 502 -> 之前就因此构建失败过。
        // 官方仓库（dl.google.com / repo1.maven.org）国内外都能访问，放前面更稳。
        google()
        mavenCentral()
        maven("https://maven.aliyun.com/repository/gradle-plugin")
        maven("https://maven.aliyun.com/repository/public")
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        maven("https://maven.aliyun.com/repository/public")
        maven("https://maven.aliyun.com/repository/google")
    }
}

include(":app")
rootProject.name = "BroadcastApp"
