package com.shivendra.flyshelf

import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.widget.Toast
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

class ScreenshotObserver(private val context: Context) : ContentObserver(Handler(Looper.getMainLooper())) {

    private var lastScreenshotTime = 0L

    companion object {
        /** The last detected screenshot absolute path — polled by React Native JS */
        @Volatile
        var lastDetectedScreenshotPath: String = ""

        /** PC URL for native auto-upload (set from JS via AdvanceOverlayModule) */
        @Volatile
        var pcUrl: String = ""

        /** Device name for upload headers */
        @Volatile
        var deviceName: String = "Mobile"
    }

    override fun onChange(selfChange: Boolean, uri: Uri?) {
        super.onChange(selfChange, uri)
        if (uri == null) return
        
        val now = System.currentTimeMillis()
        if (now - lastScreenshotTime < 3000) return

        try {
            val cursor = context.contentResolver.query(
                MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
                arrayOf(MediaStore.Images.Media.DATA, MediaStore.Images.Media.DATE_ADDED),
                null, null,
                "${MediaStore.Images.Media.DATE_ADDED} DESC"
            )
            cursor?.use {
                if (it.moveToFirst()) {
                    val path = it.getString(0) ?: return
                    val dateAdded = it.getLong(1)
                    if (System.currentTimeMillis() / 1000 - dateAdded < 5) {
                        val lower = path.lowercase()
                        if (lower.contains("screenshot") || lower.contains("screen_shot") || lower.contains("screen shot")) {
                            lastScreenshotTime = now
                            lastDetectedScreenshotPath = path
                            
                            Handler(Looper.getMainLooper()).post {
                                Toast.makeText(context, "📸 Screenshot detected — syncing...", Toast.LENGTH_SHORT).show()
                            }

                            // Auto-upload to PC in background if URL is available
                            if (pcUrl.isNotEmpty()) {
                                Thread {
                                    try {
                                        uploadScreenshotToPC(path)
                                    } catch (e: Exception) {
                                        // Upload failed silently — JS poller will pick it up as fallback
                                    }
                                }.start()
                            }
                        }
                    }
                }
            }
        } catch (e: Exception) {}
    }

    /**
     * Upload screenshot directly to PC from the native foreground service.
     * This works even when the React Native JS thread is suspended (app backgrounded).
     */
    private fun uploadScreenshotToPC(filePath: String) {
        val file = File(filePath)
        if (!file.exists() || file.length() == 0L) return

        val fileName = file.name
        val encodedName = URLEncoder.encode(fileName, "UTF-8")
        val encodedDevice = URLEncoder.encode(deviceName, "UTF-8")
        val uploadUrl = "${pcUrl}/api/sync_file?name=$encodedName&type=ImageLink&sourceDevice=$encodedDevice"

        val boundary = "----FlyShelfBoundary${System.currentTimeMillis()}"
        val conn = URL(uploadUrl).openConnection() as HttpURLConnection
        conn.requestMethod = "POST"
        conn.doOutput = true
        conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
        conn.setRequestProperty("X-FlyShelf-Client", "MobileCompanion")
        // Read pairing key from SharedPreferences and attach as auth header
        try {
            val masterKey = MasterKey.Builder(context)
                .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                .build()
            val prefs = EncryptedSharedPreferences.create(
                context,
                "flyshelf_secure_prefs",
                masterKey,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
            )
            val pairingKey = prefs.getString("flyshelf_pairing_key", "") ?: ""
            if (pairingKey.isNotEmpty()) {
                conn.setRequestProperty("X-Pairing-Key", pairingKey)
            }
        } catch (e: Exception) {}
        conn.connectTimeout = 5000
        conn.readTimeout = 15000

        conn.outputStream.use { out ->
            val writer = out.bufferedWriter()
            writer.write("--$boundary\r\n")
            writer.write("Content-Disposition: form-data; name=\"file\"; filename=\"$fileName\"\r\n")
            writer.write("Content-Type: image/png\r\n\r\n")
            writer.flush()

            file.inputStream().use { input ->
                input.copyTo(out, bufferSize = 65536)
            }

            writer.write("\r\n--$boundary--\r\n")
            writer.flush()
        }

        val responseCode = conn.responseCode
        conn.disconnect()

        if (responseCode == 200) {
            Handler(Looper.getMainLooper()).post {
                Toast.makeText(context, "📸 Screenshot synced to PC!", Toast.LENGTH_SHORT).show()
            }
        }
    }
}
