package com.shivendra.flyshelf

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import android.app.PendingIntent
import android.content.ClipboardManager
import android.content.ClipData
import android.widget.Toast

class FlyShelfWidgetProvider : AppWidgetProvider() {

    companion object {
        const val ACTION_COPY_CLIP = "com.shivendra.flyshelf.WIDGET_COPY"
        const val EXTRA_CLIP_TEXT = "clip_text"

        fun updateAllWidgets(context: Context) {
            val intent = Intent(context, FlyShelfWidgetProvider::class.java)
            intent.action = AppWidgetManager.ACTION_APPWIDGET_UPDATE
            val ids = AppWidgetManager.getInstance(context)
                .getAppWidgetIds(android.content.ComponentName(context, FlyShelfWidgetProvider::class.java))
            intent.putExtra(AppWidgetManager.EXTRA_APPWIDGET_IDS, ids)
            context.sendBroadcast(intent)
        }
    }

    override fun onUpdate(context: Context, appWidgetManager: AppWidgetManager, appWidgetIds: IntArray) {
        for (id in appWidgetIds) {
            updateWidget(context, appWidgetManager, id)
        }
    }

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == ACTION_COPY_CLIP) {
            val text = intent.getStringExtra(EXTRA_CLIP_TEXT) ?: return
            try {
                val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(ClipData.newPlainText("FlyShelf", text))
                Toast.makeText(context, "\uD83D\uDCCB Copied!", Toast.LENGTH_SHORT).show()
            } catch (e: Exception) {}
        }
    }

    private fun updateWidget(context: Context, manager: AppWidgetManager, widgetId: Int) {
        val views = RemoteViews(context.packageName, R.layout.widget_flyshelf)

        // Open app on header tap
        val openIntent = context.packageManager.getLaunchIntentForPackage(context.packageName)
        if (openIntent != null) {
            val openPending = PendingIntent.getActivity(context, 0, openIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            views.setOnClickPendingIntent(R.id.widget_header, openPending)
        }

        // Read clips from OverlayService companion
        try {
            val arr = org.json.JSONArray(OverlayService.clipboardItems)
            val clipIds = arrayOf(R.id.clip_1, R.id.clip_2, R.id.clip_3)
            for (i in clipIds.indices) {
                if (i < arr.length()) {
                    val obj = arr.getJSONObject(i)
                    val raw = obj.optString("Raw", obj.optString("Title", ""))
                    val display = if (raw.length > 80) raw.take(77) + "..." else raw
                    views.setTextViewText(clipIds[i], display)
                    views.setViewVisibility(clipIds[i], android.view.View.VISIBLE)

                    // Copy on tap
                    val copyIntent = Intent(context, FlyShelfWidgetProvider::class.java).apply {
                        action = ACTION_COPY_CLIP
                        putExtra(EXTRA_CLIP_TEXT, raw)
                    }
                    val copyPending = PendingIntent.getBroadcast(context, i + widgetId * 10, copyIntent,
                        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
                    views.setOnClickPendingIntent(clipIds[i], copyPending)
                } else {
                    views.setViewVisibility(clipIds[i], android.view.View.GONE)
                }
            }
        } catch (e: Exception) {
            views.setTextViewText(R.id.clip_1, "Open FlyShelf to sync clips")
        }

        manager.updateAppWidget(widgetId, views)
    }
}
