plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.phonecam"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.phonecam"
        minSdk = 30
        targetSdk = 35
        versionCode = 5
        versionName = "0.2.4"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // scans the pairing QR code shown by the PC app (no camera permission screen of its own)
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
}
