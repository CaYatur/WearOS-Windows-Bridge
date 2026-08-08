package dev.caya.wearbridge

import android.content.Context
import android.content.pm.PackageManager

enum class DeviceMode { PHONE_COMPANION, WEAR_OS }

object DeviceModeDetector {
    fun detect(context: Context): DeviceMode =
        if (context.packageManager.hasSystemFeature(PackageManager.FEATURE_WATCH)) DeviceMode.WEAR_OS
        else DeviceMode.PHONE_COMPANION
}
