plugins {
    id("com.android.application")
}

android {
    namespace = "com.clgame2phone.client"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.clgame2phone.client"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "0.1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
