package com.shivendra.flyshelf

import android.app.Activity
import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.OpenableColumns
import com.facebook.react.bridge.*
import com.facebook.react.modules.core.DeviceEventManagerModule
import java.io.File
import java.io.FileOutputStream

class ShareIntentModule(reactContext: ReactApplicationContext) :
    ReactContextBaseJavaModule(reactContext), ActivityEventListener {

    init {
        reactContext.addActivityEventListener(this)
        instance = this
    }

    override fun getName(): String = "ShareIntent"

    override fun onNewIntent(intent: Intent) {
        pendingIntent = intent
        emitNewShareEvent()
    }

    override fun onActivityResult(activity: Activity, requestCode: Int, resultCode: Int, data: Intent?) {}

    private fun emitNewShareEvent() {
        try {
            if (reactApplicationContext.hasActiveReactInstance()) {
                reactApplicationContext
                    .getJSModule(DeviceEventManagerModule.RCTDeviceEventEmitter::class.java)
                    .emit("onShareIntentReceived", null)
            }
        } catch (e: Exception) {}
    }

    @ReactMethod
    fun getSharedFiles(promise: Promise) {
        val intent = pendingIntent ?: reactApplicationContext.currentActivity?.intent
        if (intent == null) {
            promise.resolve(null)
            return
        }

        val action = intent.action
        if (action != Intent.ACTION_SEND && action != Intent.ACTION_SEND_MULTIPLE) {
            promise.resolve(null)
            return
        }

        try {
            val resultMap = Arguments.createMap()
            val filesArray = Arguments.createArray()

            var sharedText: String? = null
            if (intent.hasExtra(Intent.EXTRA_TEXT)) {
                sharedText = intent.getStringExtra(Intent.EXTRA_TEXT)
            } else if (intent.hasExtra(Intent.EXTRA_SUBJECT)) {
                sharedText = intent.getStringExtra(Intent.EXTRA_SUBJECT)
            }
            if (sharedText != null) {
                resultMap.putString("text", sharedText)
            }

            val uris = mutableListOf<Uri>()
            if (action == Intent.ACTION_SEND) {
                val uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
                } ?: intent.data

                if (uri != null) uris.add(uri)
            } else if (action == Intent.ACTION_SEND_MULTIPLE) {
                val list = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)
                }
                if (list != null) uris.addAll(list)
            }

            val context = reactApplicationContext
            val contentResolver = context.contentResolver

            for (uri in uris) {
                val fileInfo = copyContentUriToCache(context, contentResolver, uri, intent.type)
                if (fileInfo != null) {
                    val fileMap = Arguments.createMap().apply {
                        putString("uri", fileInfo.uri)
                        putString("fileName", fileInfo.fileName)
                        putString("mimeType", fileInfo.mimeType)
                        putDouble("size", fileInfo.size.toDouble())
                    }
                    filesArray.pushMap(fileMap)
                }
            }

            resultMap.putArray("files", filesArray)
            promise.resolve(resultMap)
        } catch (e: Exception) {
            promise.reject("SHARE_ERROR", e.message, e)
        }
    }

    @ReactMethod
    fun clearIntent() {
        pendingIntent = null
        reactApplicationContext.currentActivity?.intent?.let {
            it.action = ""
            it.removeExtra(Intent.EXTRA_TEXT)
            it.removeExtra(Intent.EXTRA_STREAM)
        }
    }

    private data class FileInfo(val uri: String, val fileName: String, val mimeType: String, val size: Long)

    private fun copyContentUriToCache(
        context: Context,
        resolver: ContentResolver,
        uri: Uri,
        intentMimeType: String?
    ): FileInfo? {
        var fileName = "shared_file_${System.currentTimeMillis()}"
        var fileSize: Long = 0
        var mimeType = intentMimeType ?: "application/octet-stream"

        try {
            resolver.query(uri, null, null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (nameIdx != -1) {
                        cursor.getString(nameIdx)?.let { fileName = it }
                    }
                    if (sizeIdx != -1 && !cursor.isNull(sizeIdx)) {
                        fileSize = cursor.getLong(sizeIdx)
                    }
                }
            }
        } catch (e: Exception) {}

        resolver.getType(uri)?.let { mimeType = it }

        val cleanName = fileName.replace(Regex("[^a-zA-Z0-9._-]"), "_")
        val cacheFile = File(context.cacheDir, "share_${System.currentTimeMillis()}_$cleanName")

        try {
            resolver.openInputStream(uri)?.use { input ->
                FileOutputStream(cacheFile).use { output ->
                    input.copyTo(output)
                }
            }
            if (fileSize == 0L) {
                fileSize = cacheFile.length()
            }
            return FileInfo(
                uri = Uri.fromFile(cacheFile).toString(),
                fileName = fileName,
                mimeType = mimeType,
                size = fileSize
            )
        } catch (e: Exception) {
            return null
        }
    }

    companion object {
        var instance: ShareIntentModule? = null
        var pendingIntent: Intent? = null

        fun onNewIntent(intent: Intent?) {
            pendingIntent = intent
            instance?.emitNewShareEvent()
        }
    }
}
