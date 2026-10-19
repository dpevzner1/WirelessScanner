import java.util.Properties
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

// Same feature version as the Windows app it ports (Directory.Build.props); versionCode = major*10000 + minor*100 + patch.
val appVersionName = "1.5.0"
val appVersionCode = 10500

// Sideload signing key: keeps the same signature across builds so updates install over earlier ones. Not a store key.
// The keystore and its passwords never enter source control: they are read from AndroidApp/local.properties
// (sideload.storeFile / sideload.storePassword / sideload.keyAlias / sideload.keyPassword) or from the environment
// (WS_SIDELOAD_STORE_FILE / _STORE_PASSWORD / _KEY_ALIAS / _KEY_PASSWORD). Without them, debug builds use the
// Android debug key and release builds are unsigned. See docs/DEPLOYMENT.md, "Signing".
val localProps = Properties().apply {
    val f = rootProject.file("local.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}
fun secret(prop: String, env: String): String? = localProps.getProperty(prop) ?: System.getenv(env)
val sideloadStore = secret("sideload.storeFile", "WS_SIDELOAD_STORE_FILE")?.let { rootProject.file(it) }?.takeIf { it.exists() }
val sideloadStorePassword = secret("sideload.storePassword", "WS_SIDELOAD_STORE_PASSWORD")
val sideloadKeyPassword = secret("sideload.keyPassword", "WS_SIDELOAD_KEY_PASSWORD")
val canSign = sideloadStore != null && sideloadStorePassword != null && sideloadKeyPassword != null


android {
    namespace = "com.wirelessscanner.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.wirelessscanner.android"
        minSdk = 26
        targetSdk = 36
        versionCode = appVersionCode
        versionName = appVersionName
    }

    signingConfigs {
        if (canSign) create("sideload") {
            storeFile = sideloadStore
            storePassword = sideloadStorePassword
            keyAlias = secret("sideload.keyAlias", "WS_SIDELOAD_KEY_ALIAS") ?: "sideload"
            keyPassword = sideloadKeyPassword
        }
    }

    buildTypes {
        debug {
            if (canSign) signingConfig = signingConfigs.getByName("sideload")
        }
        release {
            isMinifyEnabled = false
            if (canSign) signingConfig = signingConfigs.getByName("sideload")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}")
    }

    lint {
        abortOnError = false
    }
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    implementation(project(":core"))
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.kotlinx.coroutines.android)
    debugImplementation(libs.androidx.compose.ui.tooling)
    testImplementation(libs.junit4)
}
