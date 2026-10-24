import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing key, kept out of git: ../keys/keystore.properties
val keyProps = Properties().apply {
    val f = rootProject.file("../keys/keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}
val appVersion = (project.findProperty("appVersion") as String?) ?: "1.1.0"

android {
    namespace = "fr.adam.nasreader"
    compileSdk = 35

    defaultConfig {
        applicationId = "fr.adam.nasreader"
        minSdk = 26
        targetSdk = 35
        versionName = appVersion
        versionCode = appVersion.split('.').map { it.toInt() }.let { it[0] * 10000 + it[1] * 100 + it[2] }
    }

    // The interface is shared with the Windows app: one index.html for both.
    sourceSets["main"].assets.srcDirs("src/main/assets", "../../src/NasReader/wwwroot")

    signingConfigs {
        create("release") {
            if (keyProps.isNotEmpty()) {
                storeFile = rootProject.file("../keys/" + keyProps.getProperty("storeFile"))
                storePassword = keyProps.getProperty("storePassword")
                keyAlias = keyProps.getProperty("keyAlias")
                keyPassword = keyProps.getProperty("keyPassword")
            }
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (keyProps.isNotEmpty()) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    packaging {
        resources.excludes += setOf("META-INF/*.md", "META-INF/LICENSE*", "META-INF/NOTICE*", "META-INF/versions/**", "META-INF/DEPENDENCIES", "META-INF/INDEX.LIST")
    }
}

dependencies {
    implementation("org.nanohttpd:nanohttpd:2.3.1")
    implementation("com.hierynomus:smbj:0.13.0")
    implementation("me.zhanghai.android.libarchive:library:1.1.4")
    implementation("androidx.core:core:1.13.1")
}
