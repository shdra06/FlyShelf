package com.shivendra.flyshelf

import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.net.Uri
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod
import com.facebook.react.bridge.Promise
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

class AdvanceOverlayModule(reactContext: ReactApplicationContext) : ReactContextBaseJavaModule(reactContext) {

    override fun getName(): String = "AdvanceOverlay"

    @ReactMethod
    fun startOverlay() {
        val context = reactApplicationContext
        val intent = Intent(context, OverlayService::class.java)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        } catch (e: Exception) {
            android.util.Log.e("AdvanceOverlay", "Failed to start overlay service: ${e.message}")
        }
    }

    @ReactMethod
    fun setBallVisible(visible: Boolean) {
        OverlayService.isBallVisible = visible
        OverlayService.instance?.setBallVisibility(visible)
    }

    @ReactMethod
    fun stopOverlay() {
        val context = reactApplicationContext
        context.stopService(Intent(context, OverlayService::class.java))
    }

    @ReactMethod
    fun checkOverlayPermission(promise: Promise) {
        val context = reactApplicationContext
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            promise.resolve(Settings.canDrawOverlays(context))
        } else {
            promise.resolve(true)
        }
    }

    @ReactMethod
    fun requestOverlayPermission() {
        val context = reactApplicationContext
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            context.startActivity(intent)
        }
    }

    @ReactMethod
    fun syncNativeDB(jsonStr: String) {
        OverlayService.clipboardItems = jsonStr
        OverlayService.instance?.pulseBall()
    }

    @ReactMethod
    fun pushClipToNativeDB(rawText: String, source: String) {
        try {
            val arr = org.json.JSONArray(OverlayService.clipboardItems)
            val obj = org.json.JSONObject()
            obj.put("Raw", rawText)
            obj.put("Title", rawText.take(60))
            obj.put("Source", source)
            arr.put(0, obj)
            while (arr.length() > 50) arr.remove(arr.length() - 1)
            OverlayService.clipboardItems = arr.toString()
        } catch(e: Exception) {}
    }

    @ReactMethod
    fun setOverlayConfig(sizeDp: Int, autoHideMs: Int) {
        OverlayService.ballSizeDp = sizeDp
        OverlayService.autoHideDelayMs = autoHideMs.toLong()
    }

    @ReactMethod
    fun getLastCopiedFromOverlay(promise: Promise) {
        val last = OverlayService.lastCopiedText
        if (last.isNotEmpty()) {
            OverlayService.lastCopiedText = ""
            promise.resolve(last)
        } else {
            promise.resolve(null)
        }
    }

    @ReactMethod
    fun getLatestScreenshot(promise: Promise) {
        val path = ScreenshotObserver.lastDetectedScreenshotPath
        if (path.isNotEmpty()) {
            promise.resolve(path)
        } else {
            promise.resolve(null)
        }
    }

    @ReactMethod
    fun setPcUrl(url: String) {
        ScreenshotObserver.pcUrl = url
        reactApplicationContext.getSharedPreferences("flyshelf_service_prefs", Context.MODE_PRIVATE).edit().putString("pcUrl", url).apply()
    }

    @ReactMethod
    fun setDeviceName(name: String) {
        ScreenshotObserver.deviceName = name
    }

    @ReactMethod
    fun setPairingKey(key: String) {
        val masterKey = MasterKey.Builder(reactApplicationContext)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        val prefs = EncryptedSharedPreferences.create(
            reactApplicationContext,
            "flyshelf_secure_prefs",
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
        prefs.edit().putString("flyshelf_pairing_key", key).apply()
    }

    @ReactMethod
    fun setClipboardSuppressed(text: String) {
        try {
            val cm = reactApplicationContext.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
            OverlayService.lastCopiedText = text
            cm.setPrimaryClip(android.content.ClipData.newPlainText("FlyShelf", text))
        } catch (e: Exception) {}
    }

    @ReactMethod
    fun setSyncEnabled(enabled: Boolean) {
        val svc = OverlayService.instance ?: return
        if (enabled) svc.startNativeSync() else svc.stopNativeSync()
    }

    @ReactMethod
    fun getSyncStatus(promise: Promise) {
        promise.resolve(OverlayService.instance?.syncEnabled ?: false)
    }

    @ReactMethod
    fun getPendingClips(promise: Promise) {
        val svc = OverlayService.instance
        if (svc == null) { promise.resolve("[]"); return }
        val arr = org.json.JSONArray()
        while (true) {
            val clip = svc.pendingClips.poll() ?: break
            try { arr.put(org.json.JSONObject(clip)) } catch (e: Exception) {}
        }
        promise.resolve(arr.toString())
    }
}
