package com.shivendra.flyshelf

import android.service.quicksettings.TileService
import android.service.quicksettings.Tile
import android.content.Intent
import android.os.Build
import android.graphics.drawable.Icon

class FlyShelfTileService : TileService() {
    
    companion object {
        @Volatile var isSyncing: Boolean = false
        @Volatile var connectionType: String = "Offline" // "LAN", "Cloud", "Offline"
    }
    
    override fun onStartListening() {
        super.onStartListening()
        updateTile()
    }
    
    override fun onClick() {
        super.onClick()
        // Launch the app
        val intent = packageManager.getLaunchIntentForPackage(packageName)
        if (intent != null) {
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startActivityAndCollapse(android.app.PendingIntent.getActivity(this, 0, intent, android.app.PendingIntent.FLAG_IMMUTABLE))
            } else {
                @Suppress("DEPRECATION")
                startActivityAndCollapse(intent)
            }
        }
    }
    
    private fun updateTile() {
        val tile = qsTile ?: return
        tile.label = "FlyShelf"
        tile.subtitle = connectionType
        tile.state = if (isSyncing) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.updateTile()
    }
}
