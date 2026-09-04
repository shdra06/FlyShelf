const { withAndroidManifest, withMainApplication, withDangerousMod } = require('expo/config-plugins');
const fs = require('fs');
const path = require('path');

const SHARE_INTENT_MODULE_KT = `package com.shivendra.flyshelf

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
        var fileName = "shared_file_\${System.currentTimeMillis()}"
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
        val cacheFile = File(context.cacheDir, "share_\${System.currentTimeMillis()}_\$cleanName")

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
`;

const SHARE_INTENT_PACKAGE_KT = `package com.shivendra.flyshelf

import android.view.View
import com.facebook.react.ReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.uimanager.ReactShadowNode
import com.facebook.react.uimanager.ViewManager

class ShareIntentPackage : ReactPackage {
    override fun createNativeModules(reactContext: ReactApplicationContext): List<NativeModule> {
        return listOf(ShareIntentModule(reactContext))
    }

    override fun createViewManagers(reactContext: ReactApplicationContext): List<ViewManager<View, ReactShadowNode<*>>> {
        return emptyList()
    }
}
`;

function withShareFiles(config) {
  return withDangerousMod(config, ['android', async (config) => {
    const projectRoot = config.modRequest.projectRoot;
    const targetDir = path.join(projectRoot, 'android', 'app', 'src', 'main', 'java', 'com', 'shivendra', 'flyshelf');
    if (!fs.existsSync(targetDir)) {
      fs.mkdirSync(targetDir, { recursive: true });
    }
    fs.writeFileSync(path.join(targetDir, 'ShareIntentModule.kt'), SHARE_INTENT_MODULE_KT, 'utf8');
    fs.writeFileSync(path.join(targetDir, 'ShareIntentPackage.kt'), SHARE_INTENT_PACKAGE_KT, 'utf8');

    // Also update MainActivity.kt with onNewIntent
    const mainActivityPath = path.join(targetDir, 'MainActivity.kt');
    if (fs.existsSync(mainActivityPath)) {
      let mainActivityCode = fs.readFileSync(mainActivityPath, 'utf8');
      if (!mainActivityCode.includes('onNewIntent')) {
        const lastBraceIndex = mainActivityCode.lastIndexOf('}');
        if (lastBraceIndex !== -1) {
          const onNewIntentSnippet = `
  override fun onNewIntent(intent: android.content.Intent) {
      super.onNewIntent(intent)
      setIntent(intent)
      ShareIntentModule.onNewIntent(intent)
  }
`;
          mainActivityCode = mainActivityCode.slice(0, lastBraceIndex) + onNewIntentSnippet + mainActivityCode.slice(lastBraceIndex);
          fs.writeFileSync(mainActivityPath, mainActivityCode, 'utf8');
        }
      }
    }

    console.log('[FlyShelf] ✅ ShareIntent native Kotlin files injected successfully.');
    return config;
  }]);
}

function withSharePackageRegistration(config) {
  return withMainApplication(config, async (config) => {
    let contents = config.modResults.contents;
    if (!contents.includes('ShareIntentPackage')) {
      contents = contents.replace(
        '// Packages that cannot be autolinked yet can be added manually here, for example:',
        '// Packages that cannot be autolinked yet can be added manually here, for example:\n              add(ShareIntentPackage())'
      );
    }
    config.modResults.contents = contents;
    console.log('[FlyShelf] ✅ ShareIntentPackage registered in MainApplication.kt');
    return config;
  });
}

function withShareTarget(config) {
  config = withShareFiles(config);
  config = withSharePackageRegistration(config);
  config = withAndroidManifest(config, (mod) => {
    const manifest = mod.modResults;
    const mainActivity = manifest.manifest.application?.[0]?.activity?.find(
      (a) => a.$['android:name'] === '.MainActivity'
    );

    if (!mainActivity) {
      console.warn('[withShareTarget] Could not find .MainActivity in AndroidManifest.xml');
      return mod;
    }

    if (!mainActivity['intent-filter']) {
      mainActivity['intent-filter'] = [];
    }

    // ── Single file share (ACTION_SEND) ──
    mainActivity['intent-filter'].push({
      action: [{ $: { 'android:name': 'android.intent.action.SEND' } }],
      category: [{ $: { 'android:name': 'android.intent.category.DEFAULT' } }],
      data: [
        { $: { 'android:mimeType': 'image/*' } },
        { $: { 'android:mimeType': 'video/*' } },
        { $: { 'android:mimeType': 'application/pdf' } },
        { $: { 'android:mimeType': 'application/msword' } },
        { $: { 'android:mimeType': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' } },
        { $: { 'android:mimeType': 'application/vnd.ms-excel' } },
        { $: { 'android:mimeType': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' } },
        { $: { 'android:mimeType': 'application/vnd.ms-powerpoint' } },
        { $: { 'android:mimeType': 'application/vnd.openxmlformats-officedocument.presentationml.presentation' } },
        { $: { 'android:mimeType': 'text/*' } },
        { $: { 'android:mimeType': 'application/zip' } },
        { $: { 'android:mimeType': 'application/*' } },
      ],
    });

    // ── Multiple files share (ACTION_SEND_MULTIPLE) ──
    mainActivity['intent-filter'].push({
      action: [{ $: { 'android:name': 'android.intent.action.SEND_MULTIPLE' } }],
      category: [{ $: { 'android:name': 'android.intent.category.DEFAULT' } }],
      data: [
        { $: { 'android:mimeType': 'image/*' } },
        { $: { 'android:mimeType': 'video/*' } },
        { $: { 'android:mimeType': 'application/*' } },
        { $: { 'android:mimeType': 'text/*' } },
      ],
    });

    return mod;
  });

  return config;
}

module.exports = withShareTarget;
