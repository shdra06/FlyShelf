const { withDangerousMod, withMainApplication, withAndroidManifest, withAppBuildGradle } = require('expo/config-plugins');
const fs = require('fs');
const path = require('path');

const PACKAGE_NAME = 'com.shivendra.flyshelf';
const PACKAGE_DIR = 'com/shivendra/flyshelf';

// ====== NATIVE KOTLIN SOURCE FILES ======

const OVERLAY_SERVICE_KT = `package ${PACKAGE_NAME}

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.graphics.PixelFormat
import android.os.IBinder
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.os.Build
import android.content.ClipboardManager
import android.content.ClipData
import android.content.Context
import android.widget.Toast
import org.json.JSONArray
import android.widget.ScrollView
import android.provider.MediaStore
import android.graphics.drawable.GradientDrawable
import android.graphics.Typeface
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.graphics.drawable.LayerDrawable
import android.os.Handler
import android.os.Looper
import android.view.HapticFeedbackConstants
import android.animation.ValueAnimator

class OverlayService : Service() {

    private var windowManager: WindowManager? = null
    private var floatingBallView: View? = null
    private var panelView: View? = null
    private var dimView: View? = null
    private var isPanelVisible = false
    private var screenshotObserver: ScreenshotObserver? = null
    private var panelParams: WindowManager.LayoutParams? = null
    private var ballParams: WindowManager.LayoutParams? = null
    private val autoHideHandler = Handler(Looper.getMainLooper())
    private var autoHideRunnable: Runnable? = null
    private var clipboardListener: ClipboardManager.OnPrimaryClipChangedListener? = null
    private var lastAutoClipTime: Long = 0
    
    private var syncThread: Thread? = null
    var syncEnabled = false
    val pendingClips = java.util.concurrent.ConcurrentLinkedQueue<String>()
    private var networkCallback: android.net.ConnectivityManager.NetworkCallback? = null
    private var pendingSyncBadge = 0
    private var badgeView: TextView? = null

    companion object {
        @Volatile var clipboardItems: String = "[]"
        var ballSizeDp: Int = 48
        var autoHideDelayMs: Long = 3000L
        var lastCopiedText: String = ""
        var isBallVisible: Boolean = true
        var instance: OverlayService? = null
        const val CHANNEL_ID = "flyshelf_overlay"
        const val NOTIF_ID = 1001
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        return START_STICKY
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(CHANNEL_ID, "FlyShelf Overlay", NotificationManager.IMPORTANCE_LOW)
            channel.setShowBadge(false)
            val nm = getSystemService(NotificationManager::class.java)
            nm?.createNotificationChannel(channel)
            val notification = Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("FlyShelf Active")
                .setContentText("Background clipboard sync is running")
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .build()
            if (android.os.Build.VERSION.SDK_INT >= 34) {
                startForeground(NOTIF_ID, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            } else {
                startForeground(NOTIF_ID, notification)
            }
        }
        windowManager = getSystemService(WINDOW_SERVICE) as WindowManager
        if (isBallVisible && (Build.VERSION.SDK_INT < Build.VERSION_CODES.M || android.provider.Settings.canDrawOverlays(this))) {
            createFloatingBall()
        }
        try {
            screenshotObserver = ScreenshotObserver(this)
            contentResolver.registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, screenshotObserver!!)
        } catch(e: Exception) {}
        // Listen for system clipboard changes — auto-capture anything copied on the phone
        try {
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
                try {
                    val now = System.currentTimeMillis()
                    if (now - lastAutoClipTime < 1500) return@OnPrimaryClipChangedListener
                    lastAutoClipTime = now
                    val clip = cm.primaryClip
                    if (clip != null && clip.itemCount > 0) {
                        val text = clip.getItemAt(0).text?.toString() ?: ""
                        if (text.isNotEmpty() && text != lastCopiedText) {
                            lastCopiedText = text
                            // Also inject into the overlay's clip list
                            try {
                                val arr = org.json.JSONArray(clipboardItems)
                                // Check if already exists at top
                                if (arr.length() == 0 || arr.getJSONObject(0).optString("Raw") != text) {
                                    val obj = org.json.JSONObject()
                                    obj.put("Raw", text)
                                    obj.put("Title", text.take(60))
                                    obj.put("Type", "Text")
                                    obj.put("SourceDeviceName", "Phone")
                                    // Insert at top
                                    val newArr = org.json.JSONArray()
                                    newArr.put(obj)
                                    for (i in 0 until Math.min(arr.length(), 19)) {
                                        newArr.put(arr.getJSONObject(i))
                                    }
                                    clipboardItems = newArr.toString()
                                    pulseBall()
                                }
                            } catch(e: Exception) {}
                        }
                    }
                } catch(e: Exception) {}
            }
            cm.addPrimaryClipChangedListener(clipboardListener)
        } catch(e: Exception) {}
        registerNetworkCallback()
    }

    private fun scheduleAutoHide() {
        autoHideRunnable?.let { autoHideHandler.removeCallbacks(it) }
        autoHideRunnable = Runnable {
            floatingBallView?.let { ball ->
                // Slide toward nearest screen edge + shrink + fade
                val screenW = resources.displayMetrics.widthPixels
                val currentX = ballParams?.x ?: 0
                val targetX = if (currentX < screenW / 2) -(ball.width / 3) else screenW - ball.width * 2 / 3
                ball.animate()
                    .alpha(0.15f)
                    .scaleX(0.6f).scaleY(0.6f)
                    .setDuration(500)
                    .setInterpolator(DecelerateInterpolator())
                    .start()
                if (ballParams != null) {
                    val startX = ballParams!!.x
                    val animator = android.animation.ValueAnimator.ofInt(startX, targetX)
                    animator.duration = 500
                    animator.interpolator = DecelerateInterpolator()
                    animator.addUpdateListener { anim ->
                        try {
                            ballParams?.x = anim.animatedValue as Int
                            windowManager?.updateViewLayout(floatingBallView, ballParams)
                        } catch (e: Exception) {}
                    }
                    animator.start()
                }
            }
        }
        autoHideHandler.postDelayed(autoHideRunnable!!, autoHideDelayMs)
    }

    private fun cancelAutoHide() {
        autoHideRunnable?.let { autoHideHandler.removeCallbacks(it) }
        floatingBallView?.animate()
            ?.alpha(1f)
            ?.scaleX(1f)?.scaleY(1f)
            ?.setDuration(250)
            ?.setInterpolator(OvershootInterpolator(1.2f))
            ?.start()
    }

    private fun createFloatingBall() {
        val density = resources.displayMetrics.density
        val sizePx = (ballSizeDp * density).toInt()
        val ballContainer = FrameLayout(this)

        // Custom drawable for clipboard+sync icon
        val ballDrawable = object : android.graphics.drawable.Drawable() {
            private val bgPaint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)
            private val borderPaint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                style = android.graphics.Paint.Style.STROKE
                strokeWidth = 1.5f * density
                color = 0x40FFFFFF
            }
            private val iconPaint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                style = android.graphics.Paint.Style.STROKE
                strokeWidth = 1.8f * density
                color = 0xFFFFFFFF.toInt()
                strokeCap = android.graphics.Paint.Cap.ROUND
                strokeJoin = android.graphics.Paint.Join.ROUND
            }
            private val fillPaint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                style = android.graphics.Paint.Style.FILL
                color = 0xFFFFFFFF.toInt()
            }

            override fun draw(canvas: android.graphics.Canvas) {
                val w = bounds.width().toFloat()
                val h = bounds.height().toFloat()
                val cx = w / 2f
                val cy = h / 2f
                val r = Math.min(cx, cy) - 2f * density

                // Gradient background circle
                bgPaint.shader = android.graphics.LinearGradient(0f, 0f, w, h,
                    intArrayOf(0xFF6C63FF.toInt(), 0xFF3B82F6.toInt(), 0xFF8B5CF6.toInt()),
                    floatArrayOf(0f, 0.5f, 1f), android.graphics.Shader.TileMode.CLAMP)
                canvas.drawCircle(cx, cy, r, bgPaint)
                canvas.drawCircle(cx, cy, r, borderPaint)

                // Scale icon to fit
                val s = r / 22f

                canvas.save()
                canvas.translate(cx, cy)

                // Clipboard body (rounded rect)
                val clipRect = android.graphics.RectF(-8f*s, -6f*s, 8f*s, 12f*s)
                canvas.drawRoundRect(clipRect, 2f*s, 2f*s, iconPaint)

                // Clipboard top clip
                val clipTopRect = android.graphics.RectF(-4f*s, -9f*s, 4f*s, -5f*s)
                canvas.drawRoundRect(clipTopRect, 1.5f*s, 1.5f*s, iconPaint)

                // Clip bump (filled small rect on top)
                val bumpRect = android.graphics.RectF(-2.5f*s, -10.5f*s, 2.5f*s, -8.5f*s)
                canvas.drawRoundRect(bumpRect, 1f*s, 1f*s, fillPaint)

                // Sync arrows (two curved arrows)
                val arrowPaint = android.graphics.Paint(iconPaint)
                arrowPaint.strokeWidth = 1.6f * density

                // Top arc (clockwise)
                val arcRect1 = android.graphics.RectF(-5f*s, -2f*s, 5f*s, 8f*s)
                canvas.drawArc(arcRect1, -150f, 120f, false, arrowPaint)
                // Top arrow head
                val path1 = android.graphics.Path()
                val ax1 = 4.3f * s * Math.cos(Math.toRadians(-30.0)).toFloat()
                val ay1 = 3f * s + 5f * s * Math.sin(Math.toRadians(-30.0)).toFloat()
                path1.moveTo(ax1 - 2f*s, ay1 - 1.5f*s)
                path1.lineTo(ax1, ay1)
                path1.lineTo(ax1 - 2.5f*s, ay1 + 0.5f*s)
                canvas.drawPath(path1, arrowPaint)

                // Bottom arc (counter-clockwise)
                canvas.drawArc(arcRect1, 30f, 120f, false, arrowPaint)
                // Bottom arrow head
                val path2 = android.graphics.Path()
                val ax2 = 5f * s * Math.cos(Math.toRadians(150.0)).toFloat()
                val ay2 = 3f * s + 5f * s * Math.sin(Math.toRadians(150.0)).toFloat()
                path2.moveTo(ax2 + 2f*s, ay2 + 1.5f*s)
                path2.lineTo(ax2, ay2)
                path2.lineTo(ax2 + 2.5f*s, ay2 - 0.5f*s)
                canvas.drawPath(path2, arrowPaint)

                canvas.restore()
            }

            override fun setAlpha(alpha: Int) { bgPaint.alpha = alpha }
            override fun setColorFilter(cf: android.graphics.ColorFilter?) { bgPaint.colorFilter = cf }
            override fun getOpacity() = android.graphics.PixelFormat.TRANSLUCENT
        }

        val ball = View(this)
        ball.background = ballDrawable
        ball.elevation = 12f * density
        ballContainer.addView(ball, FrameLayout.LayoutParams(sizePx, sizePx))
        // Sync badge counter
        val badge = TextView(this)
        badge.text = ""
        badge.textSize = 8f
        badge.setTextColor(0xFFFFFFFF.toInt())
        badge.gravity = Gravity.CENTER
        badge.typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
        val badgeBgDrawable = GradientDrawable()
        badgeBgDrawable.shape = GradientDrawable.OVAL
        badgeBgDrawable.setColor(0xFFEF4444.toInt())
        badge.background = badgeBgDrawable
        badge.visibility = View.GONE
        val badgeSize = (18 * density).toInt()
        val badgeLp = FrameLayout.LayoutParams(badgeSize, badgeSize)
        badgeLp.gravity = Gravity.TOP or Gravity.END
        ballContainer.addView(badge, badgeLp)
        badgeView = badge

        val params = WindowManager.LayoutParams(
            sizePx + (6 * density).toInt(), sizePx + (6 * density).toInt(),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else WindowManager.LayoutParams.TYPE_PHONE,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, PixelFormat.TRANSLUCENT
        )
        params.gravity = Gravity.TOP or Gravity.START
        params.x = (4 * density).toInt()
        params.y = (200 * density).toInt()
        ballParams = params

        var initialX = 0; var initialY = 0; var initialTouchX = 0f; var initialTouchY = 0f; var isDragging = false
        ballContainer.setOnTouchListener { v, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> { cancelAutoHide(); initialX = params.x; initialY = params.y; initialTouchX = event.rawX; initialTouchY = event.rawY; isDragging = false; ball.animate().scaleX(0.85f).scaleY(0.85f).setDuration(100).start(); true }
                MotionEvent.ACTION_MOVE -> { val dx = (event.rawX - initialTouchX).toInt(); val dy = (event.rawY - initialTouchY).toInt(); if (Math.abs(dx) > 10 || Math.abs(dy) > 10) isDragging = true; params.x = initialX + dx; params.y = initialY + dy; try { windowManager?.updateViewLayout(floatingBallView, params) } catch(e: Exception) {}; true }
                MotionEvent.ACTION_UP -> { ball.animate().scaleX(1f).scaleY(1f).setDuration(200).setInterpolator(OvershootInterpolator()).start(); if (!isDragging) togglePanel(); scheduleAutoHide(); true }
                else -> false
            }
        }
        floatingBallView = ballContainer
        try { windowManager?.addView(floatingBallView, params) } catch(e: Exception) {}
        scheduleAutoHide()
    }

    private fun togglePanel() { if (isPanelVisible) hidePanel() else showPanel() }

    private fun showPanel() {
        if (panelView != null) return
        floatingBallView?.animate()?.alpha(0.05f)?.setDuration(300)?.start()
        // Clear badge when panel opens
        pendingSyncBadge = 0
        updateBadge()

        val density = resources.displayMetrics.density
        val panelWidth = (300 * density).toInt()
        val panelHeight = (400 * density).toInt()

        val dim = View(this)
        dim.setBackgroundColor(0x44000000)
        dim.setOnClickListener { hidePanel() }
        val dimParams = WindowManager.LayoutParams(
            WindowManager.LayoutParams.MATCH_PARENT, WindowManager.LayoutParams.MATCH_PARENT,
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else WindowManager.LayoutParams.TYPE_PHONE,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN, PixelFormat.TRANSLUCENT
        )
        dimView = dim
        try { windowManager?.addView(dimView, dimParams) } catch(e: Exception) {}
        dim.alpha = 0f; dim.animate().alpha(1f).setDuration(250).start()

        val glassBase = GradientDrawable()
        glassBase.shape = GradientDrawable.RECTANGLE
        glassBase.cornerRadius = 28f * density
        glassBase.setColor(0xE61A1D27.toInt())
        glassBase.setStroke((1 * density).toInt(), 0x30FFFFFF)
        val glassFrost = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0x18FFFFFF, 0x08FFFFFF, 0x00000000))
        glassFrost.cornerRadius = 28f * density

        val outerFrame = FrameLayout(this)
        val container = LinearLayout(this)
        container.orientation = LinearLayout.VERTICAL
        container.background = LayerDrawable(arrayOf(glassBase, glassFrost))
        container.setPadding((16 * density).toInt(), (14 * density).toInt(), (16 * density).toInt(), (14 * density).toInt())
        container.elevation = 24f * density
        container.clipToOutline = true
        container.outlineProvider = object : android.view.ViewOutlineProvider() {
            override fun getOutline(view: View, outline: android.graphics.Outline) {
                outline.setRoundRect(0, 0, view.width, view.height, 28f * density)
            }
        }

        val grabBarWrap = LinearLayout(this)
        grabBarWrap.gravity = Gravity.CENTER
        grabBarWrap.setPadding(0, 0, 0, (4 * density).toInt())
        val grabBar = View(this)
        val grabBg = GradientDrawable(); grabBg.cornerRadius = 3f * density; grabBg.setColor(0x40FFFFFF); grabBar.background = grabBg
        grabBarWrap.addView(grabBar, LinearLayout.LayoutParams((40 * density).toInt(), (5 * density).toInt()))
        container.addView(grabBarWrap)

        val headerRow = LinearLayout(this)
        headerRow.orientation = LinearLayout.HORIZONTAL
        headerRow.gravity = Gravity.CENTER_VERTICAL
        val title = TextView(this)
        title.text = "\uD83D\uDCCB FlyShelf"
        title.textSize = 15f
        title.setTextColor(0xFFFFFFFF.toInt())
        title.typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
        headerRow.addView(title, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        // Item count
        val countLabel = TextView(this)
        try {
            val arr = JSONArray(clipboardItems)
            countLabel.text = arr.length().toString() + " items"
        } catch(e: Exception) { countLabel.text = "" }
        countLabel.textSize = 11f
        countLabel.setTextColor(0x80FFFFFF.toInt())
        countLabel.setPadding(0, 0, (8 * density).toInt(), 0)
        headerRow.addView(countLabel, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT))
        val closeX = TextView(this)
        closeX.text = "\\u2715"
        closeX.textSize = 16f
        closeX.setTextColor(0x99FFFFFF.toInt())
        closeX.gravity = Gravity.CENTER
        val closeBg = GradientDrawable(); closeBg.shape = GradientDrawable.OVAL; closeBg.setColor(0x20FFFFFF); closeX.background = closeBg
        closeX.setOnClickListener { hidePanel() }
        headerRow.addView(closeX, LinearLayout.LayoutParams((28 * density).toInt(), (28 * density).toInt()))
        container.addView(headerRow)

        val divider1 = View(this)
        val div1Bg = GradientDrawable(); div1Bg.setColor(0x15FFFFFF); div1Bg.cornerRadius = 1f * density; divider1.background = div1Bg
        val divLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, (1 * density).toInt())
        divLp.topMargin = (8 * density).toInt(); divLp.bottomMargin = (6 * density).toInt()
        container.addView(divider1, divLp)

        val scrollView = ScrollView(this)
        scrollView.isVerticalScrollBarEnabled = false
        scrollView.overScrollMode = View.OVER_SCROLL_NEVER
        val clipList = LinearLayout(this)
        clipList.orientation = LinearLayout.VERTICAL

        try {
            val arr = JSONArray(clipboardItems)
            val count = Math.min(arr.length(), 15)
            for (i in 0 until count) {
                val obj = arr.getJSONObject(i)
                val raw = obj.optString("Raw", obj.optString("Title", ""))
                val clipTitle = obj.optString("Title", raw.take(60))
                val clipType = obj.optString("Type", "Text")
                val source = obj.optString("SourceDeviceName", "")
                val downloadUrl = obj.optString("DownloadUrl", "")
                val lowerTitle = clipTitle.lowercase()

                val isImage = clipType == "Image" || lowerTitle.endsWith(".png") || lowerTitle.endsWith(".jpg") || lowerTitle.endsWith(".jpeg") || lowerTitle.endsWith(".webp") || lowerTitle.endsWith(".gif") || lowerTitle.endsWith(".bmp")
                val isPdf = clipType == "Pdf" || lowerTitle.endsWith(".pdf")
                val isDoc = clipType == "Document" || lowerTitle.endsWith(".doc") || lowerTitle.endsWith(".docx") || lowerTitle.endsWith(".txt") || lowerTitle.endsWith(".rtf")
                val isArchive = clipType == "Archive" || lowerTitle.endsWith(".zip") || lowerTitle.endsWith(".rar") || lowerTitle.endsWith(".7z")
                val isFile = isImage || isPdf || isDoc || isArchive

                val clipCard = LinearLayout(this)
                clipCard.orientation = LinearLayout.HORIZONTAL
                clipCard.gravity = Gravity.CENTER_VERTICAL
                val cardBg = GradientDrawable()
                cardBg.cornerRadius = 12f * density
                cardBg.setColor(if (isImage) 0x2010B981 else if (isPdf) 0x20EF4444 else if (isDoc) 0x203B82F6 else if (isArchive) 0x20F59E0B else 0x12FFFFFF)
                clipCard.background = cardBg
                clipCard.setPadding((10 * density).toInt(), (8 * density).toInt(), (10 * density).toInt(), (8 * density).toInt())

                if (isImage) {
                    // Image thumbnail
                    val thumbView = ImageView(this)
                    thumbView.scaleType = ImageView.ScaleType.CENTER_CROP
                    val thumbBg = GradientDrawable()
                    thumbBg.cornerRadius = 8f * density
                    thumbBg.setColor(0x30FFFFFF)
                    thumbView.background = thumbBg
                    thumbView.clipToOutline = true
                    thumbView.outlineProvider = object : android.view.ViewOutlineProvider() {
                        override fun getOutline(view: View, outline: android.graphics.Outline) {
                            outline.setRoundRect(0, 0, view.width, view.height, 8f * density)
                        }
                    }
                    // Load thumbnail from file path or show placeholder
                    try {
                        val filePath = if (raw.startsWith("/")) raw else if (raw.startsWith("file://")) raw.removePrefix("file://") else ""
                        if (filePath.isNotEmpty() && java.io.File(filePath).exists()) {
                            val opts = android.graphics.BitmapFactory.Options()
                            opts.inSampleSize = 4 // Load at 1/4 size for memory efficiency
                            val bmp = android.graphics.BitmapFactory.decodeFile(filePath, opts)
                            if (bmp != null) thumbView.setImageBitmap(bmp)
                            else { thumbView.setImageResource(android.R.drawable.ic_menu_gallery); thumbView.setColorFilter(0x80FFFFFF.toInt()) }
                        } else {
                            thumbView.setImageResource(android.R.drawable.ic_menu_gallery)
                            thumbView.setColorFilter(0x80FFFFFF.toInt())
                        }
                    } catch(e: Exception) { thumbView.setImageResource(android.R.drawable.ic_menu_gallery); thumbView.setColorFilter(0x80FFFFFF.toInt()) }
                    val thumbSize = (44 * density).toInt()
                    val thumbLp = LinearLayout.LayoutParams(thumbSize, thumbSize)
                    thumbLp.rightMargin = (10 * density).toInt()
                    clipCard.addView(thumbView, thumbLp)
                } else {
                    // Type icon emoji
                    val typeIcon = TextView(this)
                    typeIcon.textSize = 18f
                    typeIcon.gravity = Gravity.CENTER
                    typeIcon.text = when {
                        isPdf -> "\\uD83D\\uDCC4"
                        isDoc -> "\\uD83D\\uDCDD"
                        isArchive -> "\\uD83D\\uDCE6"
                        else -> "\\uD83D\\uDCCB"
                    }
                    val iconLp = LinearLayout.LayoutParams((28 * density).toInt(), (28 * density).toInt())
                    iconLp.rightMargin = (8 * density).toInt()
                    clipCard.addView(typeIcon, iconLp)
                }

                // Text content column
                val textCol = LinearLayout(this)
                textCol.orientation = LinearLayout.VERTICAL
                val titleText = TextView(this)
                titleText.text = clipTitle.take(50)
                titleText.textSize = 12f
                titleText.setTextColor(0xEEFFFFFF.toInt())
                titleText.maxLines = if (isImage) 1 else 2
                titleText.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
                textCol.addView(titleText, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))

                // Subtitle: source + type
                val subtitle = TextView(this)
                val subtitleParts = mutableListOf<String>()
                if (source.isNotEmpty()) subtitleParts.add(source)
                if (isFile) {
                    val ext = clipTitle.substringAfterLast(".", "").uppercase()
                    if (ext.isNotEmpty() && ext.length <= 5) subtitleParts.add(ext)
                }
                if (!isFile) subtitleParts.add("Text")
                subtitle.text = subtitleParts.joinToString(" \\u2022 ")
                subtitle.textSize = 10f
                subtitle.setTextColor(0x70FFFFFF.toInt())
                subtitle.maxLines = 1
                subtitle.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
                val stLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                stLp.topMargin = (2 * density).toInt()
                textCol.addView(subtitle, stLp)

                clipCard.addView(textCol, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))

                // Type badge for files
                if (isFile) {
                    val typeBadge = TextView(this)
                    typeBadge.text = when {
                        isImage -> "IMG"
                        isPdf -> "PDF"
                        isDoc -> "DOC"
                        isArchive -> "ZIP"
                        else -> "FILE"
                    }
                    typeBadge.textSize = 8f
                    typeBadge.setTextColor(0xFFFFFFFF.toInt())
                    typeBadge.gravity = Gravity.CENTER
                    typeBadge.typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
                    val tbBg = GradientDrawable()
                    tbBg.cornerRadius = 6f * density
                    tbBg.setColor(when {
                        isImage -> 0xFF10B981.toInt()
                        isPdf -> 0xFFEF4444.toInt()
                        isDoc -> 0xFF3B82F6.toInt()
                        isArchive -> 0xFFF59E0B.toInt()
                        else -> 0xFF6C63FF.toInt()
                    })
                    typeBadge.background = tbBg
                    typeBadge.setPadding((6 * density).toInt(), (2 * density).toInt(), (6 * density).toInt(), (2 * density).toInt())
                    val tbLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                    tbLp.leftMargin = (6 * density).toInt()
                    clipCard.addView(typeBadge, tbLp)
                }

                // Tap: copy to clipboard
                clipCard.setOnClickListener {
                    it.animate().scaleX(0.96f).scaleY(0.96f).setDuration(60).withEndAction { it.animate().scaleX(1f).scaleY(1f).setDuration(100).start() }.start()
                    val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    if (isImage) {
                        // For images: copy the file path or download URL
                        val toCopy = if (downloadUrl.isNotEmpty()) downloadUrl else raw
                        clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", toCopy))
                        lastCopiedText = toCopy
                        Toast.makeText(this, "\\uD83D\\uDDBC Image path copied!", Toast.LENGTH_SHORT).show()
                    } else if (isPdf || isDoc || isArchive) {
                        val toCopy = if (downloadUrl.startsWith("http")) downloadUrl else raw
                        clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", toCopy))
                        lastCopiedText = toCopy
                        Toast.makeText(this, "\\uD83D\\uDCC1 File copied! Paste URL in browser to download.", Toast.LENGTH_LONG).show()
                    } else {
                        clipboard.setPrimaryClip(ClipData.newPlainText("FlyShelf", raw))
                        lastCopiedText = raw
                        Toast.makeText(this, "\\u2705 Copied to clipboard", Toast.LENGTH_SHORT).show()
                    }
                }

                // Long press: drag and drop
                clipCard.setOnLongClickListener { v ->
                    try { v.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS) } catch(e: Exception) {}
                    val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

                    if (isImage && raw.startsWith("/") && java.io.File(raw).exists()) {
                        // For local images: use file URI for drag
                        try {
                            val fileUri = androidx.core.content.FileProvider.getUriForFile(this, packageName + ".fileprovider", java.io.File(raw))
                            val dragClip = ClipData.newUri(contentResolver, "FlyShelf Image", fileUri)
                            clipboard.setPrimaryClip(dragClip)
                            val shadowBuilder = View.DragShadowBuilder(v)
                            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                                v.startDragAndDrop(dragClip, shadowBuilder, null, View.DRAG_FLAG_GLOBAL or View.DRAG_FLAG_GLOBAL_URI_READ)
                            }
                            Toast.makeText(this, "\\uD83D\\uDDBC Dragging image \\u2014 drop anywhere", Toast.LENGTH_SHORT).show()
                        } catch(e: Exception) {
                            // Fallback to text drag
                            val dragClip = ClipData.newPlainText("FlyShelf", raw)
                            val shadowBuilder = View.DragShadowBuilder(v)
                            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) v.startDragAndDrop(dragClip, shadowBuilder, null, View.DRAG_FLAG_GLOBAL or View.DRAG_FLAG_GLOBAL_URI_READ)
                        }
                    } else {
                        val dragText = if (downloadUrl.startsWith("http")) downloadUrl else raw
                        val dragClip = ClipData.newPlainText("FlyShelf", dragText)
                        clipboard.setPrimaryClip(dragClip)
                        val shadowBuilder = View.DragShadowBuilder(v)
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                            v.startDragAndDrop(dragClip, shadowBuilder, null, View.DRAG_FLAG_GLOBAL or View.DRAG_FLAG_GLOBAL_URI_READ)
                        } else {
                            @Suppress("DEPRECATION")
                            v.startDrag(dragClip, shadowBuilder, null, 0)
                        }
                        Toast.makeText(this, "\\u270B Dragging \\u2014 drop into any field", Toast.LENGTH_SHORT).show()
                    }
                    hidePanel()
                    true
                }

                val cardLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                cardLp.bottomMargin = (4 * density).toInt()
                clipList.addView(clipCard, cardLp)
            }
            if (count == 0) {
                val emptyRow = LinearLayout(this)
                emptyRow.orientation = LinearLayout.VERTICAL
                emptyRow.gravity = Gravity.CENTER
                emptyRow.setPadding(0, (40 * density).toInt(), 0, (40 * density).toInt())
                val emptyIcon = TextView(this)
                emptyIcon.text = "\\uD83D\\uDCED"
                emptyIcon.textSize = 32f
                emptyIcon.gravity = Gravity.CENTER
                emptyRow.addView(emptyIcon)
                val emptyText = TextView(this)
                emptyText.text = "No clips synced yet\\nCopy something on your PC!"
                emptyText.textSize = 13f
                emptyText.setTextColor(0x60FFFFFF.toInt())
                emptyText.gravity = Gravity.CENTER
                emptyText.typeface = Typeface.create("sans-serif", Typeface.ITALIC)
                val etLp = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                etLp.topMargin = (8 * density).toInt()
                emptyRow.addView(emptyText, etLp)
                clipList.addView(emptyRow)
            }
        } catch(e: Exception) {}

        scrollView.addView(clipList)
        container.addView(scrollView, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
        outerFrame.addView(container, FrameLayout.LayoutParams(panelWidth, panelHeight))

        val pParams = WindowManager.LayoutParams(
            panelWidth, panelHeight,
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else WindowManager.LayoutParams.TYPE_PHONE,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE, PixelFormat.TRANSLUCENT
        )
        pParams.gravity = Gravity.TOP or Gravity.START
        pParams.x = (resources.displayMetrics.widthPixels - panelWidth) / 2
        pParams.y = (resources.displayMetrics.heightPixels - panelHeight) / 2
        panelParams = pParams
        panelView = outerFrame
        try { windowManager?.addView(panelView, pParams) } catch(e: Exception) {}
        isPanelVisible = true

        outerFrame.scaleX = 0.7f; outerFrame.scaleY = 0.7f; outerFrame.alpha = 0f
        outerFrame.animate().scaleX(1f).scaleY(1f).alpha(1f).setDuration(300).setInterpolator(OvershootInterpolator(0.8f)).start()

        var pInitialX = 0; var pInitialY = 0; var pInitialTouchX = 0f; var pInitialTouchY = 0f; var pIsDragging = false
        val headerDragListener = View.OnTouchListener { v, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> { pInitialX = pParams.x; pInitialY = pParams.y; pInitialTouchX = event.rawX; pInitialTouchY = event.rawY; pIsDragging = false; true }
                MotionEvent.ACTION_MOVE -> { val dx = (event.rawX - pInitialTouchX).toInt(); val dy = (event.rawY - pInitialTouchY).toInt(); if (Math.abs(dx) > 5 || Math.abs(dy) > 5) pIsDragging = true; if (pIsDragging) { pParams.x = pInitialX + dx; pParams.y = pInitialY + dy; try { windowManager?.updateViewLayout(panelView, pParams) } catch(e: Exception) {} }; true }
                MotionEvent.ACTION_UP -> { true }
                else -> false
            }
        }
        grabBarWrap.setOnTouchListener(headerDragListener)
        headerRow.setOnTouchListener(headerDragListener)
    }

    private fun hidePanel() {
        val panel = panelView; val dim = dimView
        if (panel != null) { panel.animate().scaleX(0.85f).scaleY(0.85f).alpha(0f).setDuration(200).setInterpolator(DecelerateInterpolator()).withEndAction { try { windowManager?.removeView(panel) } catch(e: Exception) {} }.start()
        } else { try { if (panelView != null) windowManager?.removeView(panelView) } catch(e: Exception) {} }
        if (dim != null) { dim.animate().alpha(0f).setDuration(200).withEndAction { try { windowManager?.removeView(dim) } catch(e: Exception) {} }.start()
        } else { try { if (dimView != null) windowManager?.removeView(dimView) } catch(e: Exception) {} }
        panelView = null; dimView = null; panelParams = null; isPanelVisible = false
        floatingBallView?.animate()?.alpha(1f)?.setDuration(300)?.start()
        scheduleAutoHide()
    }

    fun setBallVisibility(visible: Boolean) {
        isBallVisible = visible
        Handler(Looper.getMainLooper()).post {
            try {
                if (visible) {
                    if (floatingBallView == null) {
                        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M || android.provider.Settings.canDrawOverlays(this)) {
                            createFloatingBall()
                        }
                    } else {
                        floatingBallView?.visibility = View.VISIBLE
                    }
                } else {
                    floatingBallView?.visibility = View.GONE
                }
            } catch (e: Exception) {}
        }
    }

    fun pulseBall() {
        if (!isBallVisible) return
        Handler(Looper.getMainLooper()).post {
            pendingSyncBadge++
            updateBadge()
            floatingBallView?.let { ball ->
                cancelAutoHide()
                ball.animate().scaleX(1.3f).scaleY(1.3f).setDuration(150).setInterpolator(OvershootInterpolator())
                    .withEndAction {
                        ball.animate().scaleX(1f).scaleY(1f).setDuration(250).setInterpolator(OvershootInterpolator()).start()
                        scheduleAutoHide()
                    }.start()
            }
        }
    }

    private fun updateBadge() {
        Handler(Looper.getMainLooper()).post {
            badgeView?.let { bv ->
                if (pendingSyncBadge > 0) {
                    bv.text = if (pendingSyncBadge > 9) "9+" else pendingSyncBadge.toString()
                    bv.visibility = View.VISIBLE
                    bv.animate().scaleX(1.2f).scaleY(1.2f).setDuration(100).withEndAction {
                        bv.animate().scaleX(1f).scaleY(1f).setDuration(100).start()
                    }.start()
                } else {
                    bv.visibility = View.GONE
                }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        instance = null
        stopNativeSync()
        try {
            networkCallback?.let {
                val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
                cm.unregisterNetworkCallback(it)
            }
        } catch (e: Exception) {}
        autoHideRunnable?.let { autoHideHandler.removeCallbacks(it) }
        try { val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager; clipboardListener?.let { cm.removePrimaryClipChangedListener(it) } } catch(e: Exception) {}
        clipboardListener = null
        try { if (panelView != null) windowManager?.removeView(panelView) } catch(e: Exception) {}
        panelView = null
        try { if (dimView != null) windowManager?.removeView(dimView) } catch(e: Exception) {}
        dimView = null; panelParams = null; isPanelVisible = false
        try { if (floatingBallView != null) windowManager?.removeView(floatingBallView) } catch(e: Exception) {}
        floatingBallView = null
        try { if (screenshotObserver != null) contentResolver.unregisterContentObserver(screenshotObserver!!) } catch(e: Exception) {}
        screenshotObserver = null
    }

    fun startNativeSync() {
        if (syncEnabled) return
        syncEnabled = true
        syncThread = Thread {
            var backoff = 1000L
            while (syncEnabled) {
                try {
                    var url = ScreenshotObserver.pcUrl
                    if (url.isEmpty()) {
                        url = getSharedPreferences("flyshelf_service_prefs", Context.MODE_PRIVATE).getString("pcUrl", "") ?: ""
                    }
                    if (url.isEmpty()) { Thread.sleep(5000); continue }
                    
                    // Long-poll the PC for new events
                    val pollUrl = url.trimEnd('/') + "/api/events?timeout=30000"
                    val conn = java.net.URL(pollUrl).openConnection() as java.net.HttpURLConnection
                    conn.requestMethod = "GET"
                    conn.setRequestProperty("X-FlyShelf-Client", "MobileCompanion")
                    // Read pairing key from encrypted prefs
                    try {
                        val masterKey = androidx.security.crypto.MasterKey.Builder(this@OverlayService).setKeyScheme(androidx.security.crypto.MasterKey.KeyScheme.AES256_GCM).build()
                        val prefs = androidx.security.crypto.EncryptedSharedPreferences.create(this@OverlayService, "flyshelf_secure_prefs", masterKey, androidx.security.crypto.EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV, androidx.security.crypto.EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM)
                        val pk = prefs.getString("flyshelf_pairing_key", "") ?: ""
                        if (pk.isNotEmpty()) conn.setRequestProperty("X-Pairing-Key", pk)
                    } catch (e: Exception) {}
                    conn.connectTimeout = 5000
                    conn.readTimeout = 35000 // long-poll timeout
                    
                    val code = conn.responseCode
                    if (code == 200) {
                        val body = conn.inputStream.bufferedReader().readText()
                        conn.disconnect()
                        backoff = 1000L // reset backoff
                        
                        // Parse the event
                        if (body.isNotEmpty() && body != "timeout") {
                            handleNativeSyncEvent(body)
                        }
                    } else {
                        conn.disconnect()
                        Thread.sleep(backoff)
                        backoff = Math.min(backoff * 2, 30000L)
                    }
                } catch (e: Exception) {
                    Thread.sleep(backoff)
                    backoff = Math.min(backoff * 2, 30000L)
                }
            }
        }
        syncThread?.isDaemon = true
        syncThread?.start()
    }

    fun stopNativeSync() {
        syncEnabled = false
        syncThread?.interrupt()
        syncThread = null
    }

    private fun handleNativeSyncEvent(jsonBody: String) {
        try {
            val obj = org.json.JSONObject(jsonBody)
            val type = obj.optString("Type", "")
            val raw = obj.optString("Raw", obj.optString("Data", obj.optString("Title", "")))
            if (!obj.has("Raw") && obj.has("Data")) {
                obj.put("Raw", obj.getString("Data"))
            }
            val title = obj.optString("Title", raw.take(60))
            val source = obj.optString("SourceDeviceName", "PC")
            
            if (raw.isEmpty()) return
            
            // Store for JS to pick up later
            pendingClips.add(jsonBody)
            
            // Update overlay clip list
            val arr = org.json.JSONArray(clipboardItems)
            val newObj = org.json.JSONObject()
            newObj.put("Raw", raw)
            newObj.put("Title", title)
            newObj.put("Type", type)
            newObj.put("SourceDeviceName", source)
            val newArr = org.json.JSONArray()
            newArr.put(newObj)
            for (i in 0 until Math.min(arr.length(), 19)) newArr.put(arr.getJSONObject(i))
            clipboardItems = newArr.toString()
            pulseBall()
            
            // Copy to system clipboard
            if (type == "Text" || type.isEmpty()) {
                Handler(Looper.getMainLooper()).post {
                    try {
                        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        lastCopiedText = raw
                        cm.setPrimaryClip(ClipData.newPlainText("FlyShelf", raw))
                    } catch (e: Exception) {}
                }
            }
            
            // Show notification
            showSyncNotification(title, source)
            
            // Update home screen widget
            try { FlyShelfWidgetProvider.updateAllWidgets(this) } catch (e: Exception) {}
        } catch (e: Exception) {}
    }

    private fun showSyncNotification(title: String, source: String) {
        try {
            val nm = getSystemService(NotificationManager::class.java) ?: return
            // Create sync channel if not exists
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                val channel = NotificationChannel("flyshelf_sync", "Clip Sync", NotificationManager.IMPORTANCE_DEFAULT)
                channel.description = "Notifications for synced clipboard items"
                channel.setShowBadge(true)
                nm.createNotificationChannel(channel)
            }
            // "Copy" action — launches the app which gains focus, then copies
            val copyIntent = Intent(this, MainActivity::class.java).apply {
                action = "com.shivendra.flyshelf.ACTION_COPY_CLIP"
                putExtra("clip_text", title.take(2000))
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            }
            val copyPending = android.app.PendingIntent.getActivity(
                this, System.currentTimeMillis().toInt(), copyIntent,
                android.app.PendingIntent.FLAG_UPDATE_CURRENT or android.app.PendingIntent.FLAG_IMMUTABLE
            )
            val notif = Notification.Builder(this, "flyshelf_sync")
                .setContentTitle("\uD83D\uDCCB $source")
                .setContentText(title.take(100))
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setAutoCancel(true)
                .setGroup("flyshelf_clips")
                .addAction(Notification.Action.Builder(null, "📋 Copy", copyPending).build())
                .build()
            nm.notify(System.currentTimeMillis().toInt(), notif)
        } catch (e: Exception) {}
    }

    private fun registerNetworkCallback() {
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
        val request = android.net.NetworkRequest.Builder()
            .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()
        networkCallback = object : android.net.ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: android.net.Network) {
                // Network is back - restart sync immediately
                if (syncEnabled) {
                    stopNativeSync()
                    startNativeSync()
                }
            }
        }
        cm.registerNetworkCallback(request, networkCallback!!)
    }
}

`;

const OVERLAY_MODULE_KT = `package ${PACKAGE_NAME}

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
            android.util.Log.e("AdvanceOverlay", "Failed to start overlay service: " + e.message)
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
            val intent = Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:\${context.packageName}"))
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

    @ReactMethod
    fun updateQuickTile(syncing: Boolean, connectionType: String) {
        FlyShelfTileService.isSyncing = syncing
        FlyShelfTileService.connectionType = connectionType
        // Request tile update
        try {
            android.service.quicksettings.TileService.requestListeningState(
                reactApplicationContext,
                android.content.ComponentName(reactApplicationContext, FlyShelfTileService::class.java)
            )
        } catch (e: Exception) {}
    }
}
`;

const OVERLAY_PACKAGE_KT = `package ${PACKAGE_NAME}

import com.facebook.react.ReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.uimanager.ViewManager

class AdvanceOverlayPackage : ReactPackage {
    override fun createNativeModules(reactContext: ReactApplicationContext): List<NativeModule> {
        return listOf(AdvanceOverlayModule(reactContext))
    }

    override fun createViewManagers(reactContext: ReactApplicationContext): List<ViewManager<*, *>> {
        return emptyList()
    }
}
`;

const SCREENSHOT_OBSERVER_KT = `package ${PACKAGE_NAME}

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
                "\${MediaStore.Images.Media.DATE_ADDED} DESC"
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
                                Toast.makeText(context, "\uD83D\uDCF8 Screenshot detected — syncing...", Toast.LENGTH_SHORT).show()
                                OverlayService.instance?.pulseBall()
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
        val uploadUrl = "\${pcUrl}/api/sync_file?name=\$encodedName&type=ImageLink&sourceDevice=\$encodedDevice"

        val boundary = "----FlyShelfBoundary\${System.currentTimeMillis()}"
        val conn = URL(uploadUrl).openConnection() as HttpURLConnection
        conn.requestMethod = "POST"
        conn.doOutput = true
        conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=\$boundary")
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
            writer.write("--\$boundary\\r\\n")
            writer.write("Content-Disposition: form-data; name=\\"file\\"; filename=\\"\$fileName\\"\\r\\n")
            writer.write("Content-Type: image/png\\r\\n\\r\\n")
            writer.flush()

            file.inputStream().use { input ->
                input.copyTo(out, bufferSize = 65536)
            }

            writer.write("\\r\\n--\$boundary--\\r\\n")
            writer.flush()
        }

        val responseCode = conn.responseCode
        conn.disconnect()

        if (responseCode == 200) {
            Handler(Looper.getMainLooper()).post {
                Toast.makeText(context, "\uD83D\uDCF8 Screenshot synced to PC!", Toast.LENGTH_SHORT).show()
            }
        }
    }
}
`;

// ====== PLUGIN LOGIC ======

// ── Home Screen Widget ──

const WIDGET_PROVIDER_KT = `package ${PACKAGE_NAME}

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
                Toast.makeText(context, "\\uD83D\\uDCCB Copied!", Toast.LENGTH_SHORT).show()
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
`;

const WIDGET_LAYOUT_XML = `<?xml version="1.0" encoding="utf-8"?>
<LinearLayout xmlns:android="http://schemas.android.com/apk/res/android"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:orientation="vertical"
    android:background="@drawable/widget_bg"
    android:padding="12dp">

    <LinearLayout
        android:id="@+id/widget_header"
        android:layout_width="match_parent"
        android:layout_height="wrap_content"
        android:orientation="horizontal"
        android:gravity="center_vertical"
        android:paddingBottom="8dp">
        <TextView
            android:layout_width="0dp"
            android:layout_height="wrap_content"
            android:layout_weight="1"
            android:text="📋 FlyShelf"
            android:textColor="#FFFFFF"
            android:textSize="14sp"
            android:textStyle="bold" />
        <TextView
            android:layout_width="wrap_content"
            android:layout_height="wrap_content"
            android:text="Tap to copy ›"
            android:textColor="#888888"
            android:textSize="10sp" />
    </LinearLayout>

    <TextView
        android:id="@+id/clip_1"
        android:layout_width="match_parent"
        android:layout_height="wrap_content"
        android:background="@drawable/widget_clip_bg"
        android:padding="10dp"
        android:layout_marginBottom="4dp"
        android:text="No clips yet"
        android:textColor="#E0E0E0"
        android:textSize="13sp"
        android:maxLines="2"
        android:ellipsize="end"
        android:clickable="true" />

    <TextView
        android:id="@+id/clip_2"
        android:layout_width="match_parent"
        android:layout_height="wrap_content"
        android:background="@drawable/widget_clip_bg"
        android:padding="10dp"
        android:layout_marginBottom="4dp"
        android:textColor="#E0E0E0"
        android:textSize="13sp"
        android:maxLines="2"
        android:ellipsize="end"
        android:visibility="gone"
        android:clickable="true" />

    <TextView
        android:id="@+id/clip_3"
        android:layout_width="match_parent"
        android:layout_height="wrap_content"
        android:background="@drawable/widget_clip_bg"
        android:padding="10dp"
        android:textColor="#E0E0E0"
        android:textSize="13sp"
        android:maxLines="2"
        android:ellipsize="end"
        android:visibility="gone"
        android:clickable="true" />
</LinearLayout>
`;

const WIDGET_BG_XML = `<?xml version="1.0" encoding="utf-8"?>
<shape xmlns:android="http://schemas.android.com/apk/res/android"
    android:shape="rectangle">
    <solid android:color="#CC1A1A2E" />
    <corners android:radius="16dp" />
</shape>
`;

const WIDGET_CLIP_BG_XML = `<?xml version="1.0" encoding="utf-8"?>
<shape xmlns:android="http://schemas.android.com/apk/res/android"
    android:shape="rectangle">
    <solid android:color="#332A2F3A" />
    <corners android:radius="10dp" />
</shape>
`;

const WIDGET_INFO_XML = `<?xml version="1.0" encoding="utf-8"?>
<appwidget-provider xmlns:android="http://schemas.android.com/apk/res/android"
    android:minWidth="250dp"
    android:minHeight="110dp"
    android:updatePeriodMillis="1800000"
    android:initialLayout="@layout/widget_flyshelf"
    android:resizeMode="horizontal|vertical"
    android:widgetCategory="home_screen"
    android:previewImage="@mipmap/ic_launcher"
    android:description="@string/app_name" />
`;

// ── Tile Service ──
const TILE_SERVICE_KT = `package ${PACKAGE_NAME}

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
`;

function withOverlayServiceFiles(config) {
  return withDangerousMod(config, ['android', async (config) => {
    const projectRoot = config.modRequest.projectRoot;
    const javaDir = path.join(projectRoot, 'android', 'app', 'src', 'main', 'java', ...PACKAGE_DIR.split('/'));

    // Ensure directory exists
    fs.mkdirSync(javaDir, { recursive: true });

    // Write all Kotlin files
    fs.writeFileSync(path.join(javaDir, 'OverlayService.kt'), OVERLAY_SERVICE_KT);
    fs.writeFileSync(path.join(javaDir, 'AdvanceOverlayModule.kt'), OVERLAY_MODULE_KT);
    fs.writeFileSync(path.join(javaDir, 'AdvanceOverlayPackage.kt'), OVERLAY_PACKAGE_KT);
    fs.writeFileSync(path.join(javaDir, 'ScreenshotObserver.kt'), SCREENSHOT_OBSERVER_KT);
    fs.writeFileSync(path.join(javaDir, 'FlyShelfTileService.kt'), TILE_SERVICE_KT);
    fs.writeFileSync(path.join(javaDir, 'FlyShelfWidgetProvider.kt'), WIDGET_PROVIDER_KT);

    // Write widget XML resources
    const resDir = path.join(projectRoot, 'android', 'app', 'src', 'main', 'res');
    const layoutDir = path.join(resDir, 'layout');
    const drawableDir = path.join(resDir, 'drawable');
    const xmlDir = path.join(resDir, 'xml');
    fs.mkdirSync(layoutDir, { recursive: true });
    fs.mkdirSync(drawableDir, { recursive: true });
    fs.mkdirSync(xmlDir, { recursive: true });
    fs.writeFileSync(path.join(layoutDir, 'widget_flyshelf.xml'), WIDGET_LAYOUT_XML);
    fs.writeFileSync(path.join(drawableDir, 'widget_bg.xml'), WIDGET_BG_XML);
    fs.writeFileSync(path.join(drawableDir, 'widget_clip_bg.xml'), WIDGET_CLIP_BG_XML);
    fs.writeFileSync(path.join(xmlDir, 'flyshelf_widget_info.xml'), WIDGET_INFO_XML);

    console.log('[FlyShelf] ✅ Native overlay + widget + tile Kotlin files injected successfully.');
    return config;
  }]);
}

function withOverlayManifest(config) {
  return withAndroidManifest(config, async (config) => {
    const manifest = config.modResults;
    const application = manifest.manifest.application[0];

    // Add FOREGROUND_SERVICE permissions if missing
    const permissions = manifest.manifest['uses-permission'] || [];
    const fgPerm = 'android.permission.FOREGROUND_SERVICE';
    const fgSpecPerm = 'android.permission.FOREGROUND_SERVICE_SPECIAL_USE';
    const postNotifPerm = 'android.permission.POST_NOTIFICATIONS';
    if (!permissions.some(p => p.$?.['android:name'] === fgPerm)) {
        permissions.push({ $: { 'android:name': fgPerm } });
    }
    if (!permissions.some(p => p.$?.['android:name'] === fgSpecPerm)) {
        permissions.push({ $: { 'android:name': fgSpecPerm } });
    }
    if (!permissions.some(p => p.$?.['android:name'] === postNotifPerm)) {
        permissions.push({ $: { 'android:name': postNotifPerm } });
    }
    manifest.manifest['uses-permission'] = permissions;

    // Register the OverlayService in manifest
    if (!application.service) application.service = [];
    const serviceExists = application.service.some(s => s.$?.['android:name'] === '.OverlayService');
    if (!serviceExists) {
        application.service.push({
            $: {
                'android:name': '.OverlayService',
                'android:exported': 'false',
                'android:foregroundServiceType': 'specialUse'
            },
            property: [{
                $: {
                    'android:name': 'android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE',
                    'android:value': 'Floating clipboard overlay for quick paste access'
                }
            }]
        });
    }

    // Register TileService
    const tileServiceExists = application.service.some(s => s.$?.['android:name'] === '.FlyShelfTileService');
    if (!tileServiceExists) {
        application.service.push({
            $: {
                'android:name': '.FlyShelfTileService',
                'android:exported': 'true',
                'android:label': 'FlyShelf Sync',
                'android:permission': 'android.permission.BIND_QUICK_SETTINGS_TILE',
                'android:icon': '@mipmap/ic_launcher'
            },
            'intent-filter': [{
                action: [{ $: { 'android:name': 'android.service.quicksettings.action.QS_TILE' } }]
            }],
            'meta-data': [{
                $: {
                    'android:name': 'android.service.quicksettings.ACTIVE_TILE',
                    'android:value': 'true'
                }
            }]
        });
    }

    // Register Widget
    if (!application.receiver) application.receiver = [];
    const widgetExists = application.receiver.some(r => r.$?.['android:name'] === '.FlyShelfWidgetProvider');
    if (!widgetExists) {
        application.receiver.push({
            $: {
                'android:name': '.FlyShelfWidgetProvider',
                'android:exported': 'true',
                'android:label': 'FlyShelf Clipboard'
            },
            'intent-filter': [{
                action: [
                    { $: { 'android:name': 'android.appwidget.action.APPWIDGET_UPDATE' } },
                    { $: { 'android:name': 'com.shivendra.flyshelf.WIDGET_COPY' } }
                ]
            }],
            'meta-data': [{
                $: {
                    'android:name': 'android.appwidget.provider',
                    'android:resource': '@xml/flyshelf_widget_info'
                }
            }]
        });
    }

    console.log('[FlyShelf] ✅ Services + Widget registered in AndroidManifest.xml');
    return config;
  });
}

function withOverlayPackageRegistration(config) {
  return withMainApplication(config, async (config) => {
    let contents = config.modResults.contents;

    // Add the package registration if not already present
    if (!contents.includes('AdvanceOverlayPackage')) {
        // Add import
        contents = contents.replace(
            'import com.facebook.react.ReactPackage',
            'import com.facebook.react.ReactPackage\nimport com.shivendra.flyshelf.AdvanceOverlayPackage'
        );

        // Add to getPackages
        contents = contents.replace(
            '// Packages that cannot be autolinked yet can be added manually here, for example:',
            '// Packages that cannot be autolinked yet can be added manually here, for example:\n              add(AdvanceOverlayPackage())'
        );
    }

    config.modResults.contents = contents;
    console.log('[FlyShelf] ✅ AdvanceOverlayPackage registered in MainApplication.kt');
    return config;
  });
}

function withSecurityCryptoDependency(config) {
  return withAppBuildGradle(config, (config) => {
    let contents = config.modResults.contents;
    if (!contents.includes('security-crypto')) {
      contents = contents.replace(
        /dependencies\s*\{/,
        'dependencies {\n    implementation "androidx.security:security-crypto:1.1.0-alpha06"'
      );
    }
    config.modResults.contents = contents;
    console.log('[FlyShelf] ✅ security-crypto dependency added to build.gradle');
    return config;
  });
}

module.exports = function withOverlayService(config) {
  config = withOverlayServiceFiles(config);
  config = withOverlayManifest(config);
  config = withOverlayPackageRegistration(config);
  config = withSecurityCryptoDependency(config);
  return config;
};
