plugins {
    id("com.android.library") version "8.13.0"
    id("org.jetbrains.kotlin.android") version "1.9.25"
}

android {
    namespace = "dev.durable.bande.foundry"
    compileSdk = 36

    defaultConfig {
        // The AAR's own manifest declares minSdk 33; a library may not go below it.
        minSdk = 33
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
    // compileOnly, not implementation: the facade is compiled against the verified binary but
    // this module does not repackage it. AGP forbids local .aar inputs when producing an .aar,
    // and the licensing decision forbids republishing the binary anyway. The consuming app
    // references the same verified file, so the classes exist once, at runtime, in the APK.
    val foundryAarDir = providers.gradleProperty("bande.foundryAarDir").get()
    compileOnly(files("$foundryAarDir/foundry-local-ipc-sdk-0.1.6.aar"))

    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
}
