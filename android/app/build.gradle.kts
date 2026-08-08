plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }

android { namespace="dev.caya.wearbridge"; compileSdk=36
    defaultConfig { applicationId="dev.caya.wearbridge"; minSdk=28; targetSdk=36; versionCode=1; versionName="0.1.0" }
    compileOptions { sourceCompatibility=JavaVersion.VERSION_17; targetCompatibility=JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget="17" }
}

dependencies {
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.media3:media3-session:1.8.0")
    implementation("androidx.media3:media3-common:1.8.0")
}
