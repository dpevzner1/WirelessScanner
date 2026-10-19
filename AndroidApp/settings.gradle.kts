pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "WirelessScannerAndroid"
// core: pure Kotlin/JVM (analytics, report model, responder protocol) - unit-tested on the JVM with parity fixtures from the C# code.
// app:  Android platform layer (Wi-Fi, probes, storage, services, UI, PDF, sharing, REST server). DR-0015.
include(":core", ":app")
