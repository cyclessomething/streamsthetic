import java.util.Properties

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
        versionCode = 7
        versionName = "0.2.6"
    }

    // Release signing: the owner's key lives outside the repository (Documents\Streamsthetic-signing, or the
    // keystore.properties named by STREAMSTHETIC_KEYSTORE). Without it the release APK is simply left unsigned,
    // so anyone can still build the project and sign it with their own key.
    val keystoreProps = file(System.getenv("STREAMSTHETIC_KEYSTORE")
        ?: "${System.getProperty("user.home")}/Documents/Streamsthetic-signing/keystore.properties")
    val releaseKey = if (keystoreProps.isFile) Properties().apply { keystoreProps.inputStream().use { load(it) } } else null
    signingConfigs {
        if (releaseKey != null) create("release") {
            storeFile = file(releaseKey.getProperty("storeFile"))
            storePassword = releaseKey.getProperty("storePassword")
            keyAlias = releaseKey.getProperty("keyAlias")
            keyPassword = releaseKey.getProperty("keyPassword")
        }
    }
    buildTypes {
        release {
            isDebuggable = false
            isMinifyEnabled = false // the code is small; shrinking would risk breaking the QR scanner for little gain
            signingConfig = signingConfigs.findByName("release")
        }
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
