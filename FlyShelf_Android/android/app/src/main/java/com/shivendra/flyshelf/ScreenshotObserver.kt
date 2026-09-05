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
    private val pendingUploads = java.util.concurrent.ConcurrentLinkedQueue<String>()

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

        /** Pairing key for auth headers */
        @Volatile
        var pairingKey: String = ""
    }

    /** Resolve pcUrl: use in-memory value, fall back to SharedPreferences */
    private fun resolvePcUrl(): String {
        if (pcUrl.isNotEmpty()) return pcUrl
        try {
            val stored = context.getSharedPreferences("flyshelf_service_prefs", Context.MODE_PRIVATE)
                .getString("pcUrl", "") ?: ""
            if (stored.isNotEmpty()) {
                pcUrl = stored
                return stored
            }
        } catch (_: Exception) {}
        return ""
    }

    /** Resolve pairing key: use in-memory value, fall back to EncryptedSharedPreferences */
    private fun resolvePairingKey(): String {
        if (pairingKey.isNotEmpty()) return pairingKey
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
            val key = prefs.getString("flyshelf_pairing_key", "") ?: ""
            if (key.isNotEmpty()) {
                pairingKey = key
                return key
            }
        } catch (_: Exception) {}
        return ""
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
                                OverlayService.instance?.pulseBall()
                            }

                            // Auto-upload to PC in background thread
                            val url = resolvePcUrl()
                            if (url.isNotEmpty()) {
                                Thread {
                                    try {
                                        uploadScreenshotToPC(path, url)
                                    } catch (e: Exception) {
                                        // Queue for retry
                                        pendingUploads.add(path)
                                        android.util.Log.w("FlyShelf", "Screenshot upload failed, queued for retry: ${e.message}")
                                    }
                                }.start()
                            } else {
                                // Queue for when URL becomes available
                                pendingUploads.add(path)
                                android.util.Log.w("FlyShelf", "Screenshot queued — no PC URL available yet")
                            }
                        }
                    }
                }
            }
        } catch (e: Exception) {}
    }

    /** Retry any pending screenshot uploads (called when pcUrl is set or connectivity changes) */
    fun retryPendingUploads() {
        val url = resolvePcUrl()
        if (url.isEmpty()) return
        Thread {
            while (pendingUploads.isNotEmpty()) {
                val path = pendingUploads.peek() ?: break
                try {
                    uploadScreenshotToPC(path, url)
                    pendingUploads.poll() // Remove on success
                } catch (e: Exception) {
                    break // Stop retrying on first failure
                }
            }
        }.start()
    }

    /**
     * Upload screenshot directly to PC from the native foreground service.
     * This works even when the React Native JS thread is suspended (app backgrounded).
     */
    private fun uploadScreenshotToPC(filePath: String, targetUrl: String) {
        val file = File(filePath)
        if (!file.exists() || file.length() == 0L) return

        val fileName = file.name
        val encodedName = URLEncoder.encode(fileName, "UTF-8")
        val encodedDevice = URLEncoder.encode(deviceName, "UTF-8")
        val uploadUrl = "${targetUrl.trimEnd('/')}/api/sync_file?name=$encodedName&type=ImageLink&sourceDevice=$encodedDevice"

        val boundary = "----FlyShelfBoundary${System.currentTimeMillis()}"
        val CRLF = "\r\n"
        val conn = URL(uploadUrl).openConnection() as HttpURLConnection
        conn.requestMethod = "POST"
        conn.doOutput = true
        conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
        conn.setRequestProperty("X-FlyShelf-Client", "MobileCompanion")
        val key = resolvePairingKey()
        if (key.isNotEmpty()) {
            conn.setRequestProperty("X-Pairing-Key", key)
        }
        conn.connectTimeout = 10000
        conn.readTimeout = 30000

        conn.outputStream.use { out ->
            val header = "--$boundary" + CRLF +
                "Content-Disposition: form-data; name=\"file\"; filename=\"$fileName\"" + CRLF +
                "Content-Type: image/png" + CRLF + CRLF
            out.write(header.toByteArray(Charsets.UTF_8))

            file.inputStream().use { input ->
                input.copyTo(out, bufferSize = 65536)
            }

            val footer = CRLF + "--$boundary--" + CRLF
            out.write(footer.toByteArray(Charsets.UTF_8))
            out.flush()
        }

        val responseCode = conn.responseCode
        conn.disconnect()

        if (responseCode == 200) {
            Handler(Looper.getMainLooper()).post {
                Toast.makeText(context, "📸 Screenshot synced to PC!", Toast.LENGTH_SHORT).show()
            }
        } else {
            throw java.io.IOException("Upload failed with HTTP $responseCode")
        }
    }
}
