package com.shilapi.xcertplay

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import com.shilapi.xcertplay.host.R
import java.net.InetSocketAddress
import java.net.Socket

/**
 * The SimHub button (#30): the dashboard chosen in SimHub (`state.dashboardUrl`) full screen.
 *
 * Returning to CarPlay (back gesture or the small "CarPlay" button) only reorders
 * [CarPlayHostActivity] to the front; the CarPlay session is never touched. While this activity
 * covers the projection, CarPlayHostActivity is merely stopped: its TextureView keeps the
 * SurfaceTexture the decoder renders into (only the hardware layer is dropped), so video shows the
 * next decoded frame on return, the same path "rigPlay home" has always used.
 *
 * In idle mode ([openIdle], #39) it shows the idle dashboard while no phone is connected
 * (`state.idleDashboardUrl`, else the main dashboard; [DashboardContent.resolveIdle]) and reports
 * `status.screen = idle`. There a tap shows the Home / Settings toolbar ([IdleToolbar]) instead of
 * the CarPlay button, and a dashboard that fails to load hands over to [OfflineIdleActivity].
 */
class DashboardActivity : ComponentActivity() {
    private val main = Handler(Looper.getMainLooper())
    private var webView: WebView? = null
    private var overlay: LinearLayout? = null
    private var overlayText: TextView? = null
    private var retryButton: Button? = null
    private var returnButton: Button? = null
    private var toolbar: IdleToolbar? = null
    private var shown: DashboardContent? = null
    private var attempt: Attempt? = null
    private val observer: () -> Unit = { render() }

    /** Showing the idle dashboard (#39) rather than the one the SimHub button opens. */
    var idleMode = false
        private set

    /** One load of a dashboard URL, with the connected-host fallback for NAT (#30). */
    private data class Attempt(val content: DashboardContent.Load, val usingFallback: Boolean, val serial: Int) {
        val url: String get() = if (usingFallback) content.fallbackUrl!! else content.url
    }
    private var serial = 0
    private var loaded = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED)
        RigTabletWindow.immersive(window, edgeToEdge = true)
        RigSessionCoordinator.init(this)
        idleMode = intent.getBooleanExtra(EXTRA_IDLE, false)
        setContentView(buildContent())
        applyMode()
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (idleMode) IdleToolbar.open(this@DashboardActivity, "home") else returnToCarPlay()
            }
        })
    }

    /** The other mode was asked for (SimHub button over the idle dashboard, or the policy after it). */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        val next = intent.getBooleanExtra(EXTRA_IDLE, false)
        if (next == idleMode) return
        idleMode = next
        applyMode()
        shown = null
        // onResume follows, which renders and lets RigPlayApplication report the new screen.
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        if (idleMode && event.actionMasked == MotionEvent.ACTION_DOWN) {
            // The idle screen that took over the home page after inactivity (#53): a tap goes back there.
            if (RigSessionCoordinator.returnFromIdle(this)) return true
            toolbar?.show()
        }
        return super.dispatchTouchEvent(event)
    }

    private fun applyMode() {
        returnButton?.visibility = if (idleMode) View.GONE else View.VISIBLE
        if (!idleMode) toolbar?.hide()
    }

    override fun onResume() {
        super.onResume()
        RigSessionCoordinator.addObserver(observer)
        render()
    }

    override fun onPause() {
        RigSessionCoordinator.removeObserver(observer)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) RigTabletWindow.immersive(window, edgeToEdge = true)
    }

    override fun onDestroy() {
        main.removeCallbacksAndMessages(null)
        toolbar?.dispose()
        webView?.apply { stopLoading(); destroy() }
        webView = null
        super.onDestroy()
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun buildContent(): View {
        val root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        val web = WebView(this).apply {
            setBackgroundColor(Color.BLACK)
            setLayerType(View.LAYER_TYPE_HARDWARE, null)
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.mediaPlaybackRequiresUserGesture = false
            settings.useWideViewPort = true
            settings.loadWithOverviewMode = true
            webViewClient = Client()
        }
        root.addView(web, FrameLayout.LayoutParams(-1, -1))
        val message = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(Color.rgb(12, 17, 27))
            setPadding(dp(48), dp(48), dp(48), dp(48))
            visibility = View.GONE
        }
        val messageText = TextView(this).apply {
            textSize = 22f; gravity = Gravity.CENTER; setTextColor(Color.rgb(241, 245, 252))
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        message.addView(messageText, LinearLayout.LayoutParams(-1, -2))
        val retry = Button(this).apply {
            text = getString(R.string.rig_dashboard_retry); isAllCaps = false; textSize = 18f
            setTextColor(Color.rgb(12, 17, 27))
            background = GradientDrawable().apply { setColor(Color.rgb(166, 200, 255)); cornerRadius = dp(20).toFloat() }
            setOnClickListener { reload() }
        }
        message.addView(retry, LinearLayout.LayoutParams(dp(240), dp(60)).apply { topMargin = dp(24) })
        root.addView(message, FrameLayout.LayoutParams(-1, -1))
        // Small, translucent, out of the way of typical dash layouts' centre.
        val back = Button(this).apply {
            isAllCaps = false; textSize = 15f; setTextColor(Color.WHITE); alpha = 0.75f
            background = GradientDrawable().apply { setColor(0x99000000.toInt()); cornerRadius = dp(18).toFloat() }
            setPadding(dp(16), 0, dp(16), 0)
            setOnClickListener { returnToCarPlay() }
        }
        root.addView(back, FrameLayout.LayoutParams(-2, dp(44), Gravity.TOP or Gravity.END).apply {
            topMargin = dp(12); marginEnd = dp(12)
        })
        webView = web; overlay = message; overlayText = messageText; retryButton = retry; returnButton = back
        toolbar = IdleToolbar(this, root)
        return root
    }

    /** Applies the current SimHub state; reloads only when the dashboard content changed. */
    private fun render() {
        returnButton?.text = getString(
            when {
                CarPlayBackgroundSession.active -> R.string.rig_dashboard_carplay
                RigSessionCoordinator.policyScreen() != null -> R.string.rig_idle_close
                CarPlayBackgroundSession.hasSession() -> R.string.rig_dashboard_carplay
                else -> R.string.rig_rigplay_home
            },
        )
        val state = RigSessionCoordinator.state
        val paired = RigSessionCoordinator.isPaired
        // In idle mode anything but a URL makes the policy switch to the rigPlay idle screen; the
        // message shows until then.
        val content = if (idleMode) DashboardContent.resolveIdle(state, paired) else DashboardContent.resolve(state, paired)
        if (content == shown) return
        shown = content
        when (content) {
            is DashboardContent.Load -> start(Attempt(content, usingFallback = false, serial = ++serial))
            DashboardContent.NoDashboard -> showMessage(getString(R.string.rig_dashboard_no_dashboard), retry = false)
            DashboardContent.ServerOff -> showMessage(getString(R.string.rig_dashboard_server_off), retry = true)
            DashboardContent.Disconnected -> showMessage(getString(R.string.rig_dashboard_disconnected), retry = false)
            DashboardContent.NotPaired -> showMessage(getString(R.string.rig_dashboard_not_paired), retry = false)
        }
    }

    private fun reload() {
        shown = null
        render()
    }

    private fun start(next: Attempt) {
        attempt = next
        loaded = false
        Log.i(TAG, "loading dashboard ${next.url}${if (next.usingFallback) " (connected host)" else ""}")
        overlay?.visibility = View.GONE
        webView?.visibility = View.VISIBLE
        webView?.loadUrl(next.url)
        val fallback = next.content.fallbackUrl
        if (!next.usingFallback && fallback != null) probe(next)
    }

    /**
     * The URL's host differs from the one the link uses (NAT): if it does not accept a TCP
     * connection within [PROBE_TIMEOUT_MS], switch to the connected host before the WebView gives up.
     */
    private fun probe(current: Attempt) {
        val (host, port) = DashboardUrls.endpoint(current.url) ?: return
        Thread({
            val reachable = runCatching {
                Socket().use { it.connect(InetSocketAddress(host, port), PROBE_TIMEOUT_MS) }
            }.isSuccess
            if (!reachable) main.post { useFallback(current, "host $host:$port not reachable within $PROBE_TIMEOUT_MS ms") }
        }, "rigplay-dash-probe").apply { isDaemon = true }.start()
    }

    private fun useFallback(failed: Attempt, reason: String) {
        if (attempt != failed || failed.usingFallback || isDestroyed) return
        if (loaded) return
        Log.i(TAG, "dashboard URL as given failed ($reason); retrying on the connected host")
        start(failed.copy(usingFallback = true, serial = ++serial))
    }

    private fun onLoadFailed(failed: Attempt, description: String) {
        if (attempt != failed) return
        if (!failed.usingFallback && failed.content.fallbackUrl != null) {
            useFallback(failed, description)
            return
        }
        Log.w(TAG, "dashboard ${failed.url} failed: $description")
        if (idleMode) {
            // The fallback chain ends at the rigPlay idle screen (#39), which says the dashboard did not load.
            OfflineIdleActivity.open(this)
            finish()
            return
        }
        showMessage(getString(R.string.rig_dashboard_unreachable, failed.url), retry = true)
    }

    private fun showMessage(message: String, retry: Boolean) {
        attempt = null
        webView?.stopLoading()
        webView?.loadUrl("about:blank")
        webView?.visibility = View.INVISIBLE
        overlayText?.text = message
        retryButton?.visibility = if (retry) View.VISIBLE else View.GONE
        overlay?.visibility = View.VISIBLE
    }

    private inner class Client : WebViewClient() {
        private var failedSerial = -1

        override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
            if (!request.isForMainFrame) return
            val current = attempt ?: return
            // Ignore the abandoned URL as given once the fallback is loading.
            if (request.url?.host?.removePrefix("[")?.removeSuffix("]") != DashboardUrls.endpoint(current.url)?.first) return
            failedSerial = current.serial
            onLoadFailed(current, "${error.errorCode} ${error.description}")
        }

        override fun onPageFinished(view: WebView, url: String) {
            val current = attempt ?: return
            if (url == "about:blank" || failedSerial == current.serial || loaded) return
            // A failed URL as given still reports onPageFinished after the switch to the fallback.
            if (DashboardUrls.endpoint(url)?.first != DashboardUrls.endpoint(current.url)?.first) return
            loaded = true
            Log.i(TAG, "dashboard loaded from ${if (current.usingFallback) "the connected host" else "the URL as given"}: ${current.url}")
        }
    }

    /**
     * Back to CarPlay without touching the session; with no phone connected, to the screen the idle
     * policy wants (#39), else as before to CarPlayHostActivity or the rigPlay home.
     */
    private fun returnToCarPlay() {
        if (!CarPlayBackgroundSession.active) {
            when (RigSessionCoordinator.policyScreen()) {
                // Same activity: the new intent switches it to idle mode in place.
                RigSessionLifecycle.PolicyScreen.IDLE_DASHBOARD -> { RigSessionCoordinator.showPolicyScreen(); return }
                null -> Unit
                else -> if (RigSessionCoordinator.showPolicyScreen()) { finish(); return }
            }
        }
        val target = if (CarPlayBackgroundSession.hasSession()) CarPlayHostActivity::class.java else RigPlayActivity::class.java
        startActivity(Intent(this, target).addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT))
        finish()
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val TAG = "rigplay-dashboard"
        private const val PROBE_TIMEOUT_MS = 2_000
        private const val EXTRA_IDLE = "com.shilapi.xcertplay.extra.IDLE_DASHBOARD"

        /** Opens the dashboard from anywhere: home button, CarPlay's OEM icon, `command showDashboard`. */
        fun open(context: Context) = start(context, idle = false)

        /** Opens the idle dashboard (#39); the screen policy's choice while no phone is connected. */
        fun openIdle(context: Context) = start(context, idle = true)

        private fun start(context: Context, idle: Boolean) {
            context.startActivity(
                Intent(context, DashboardActivity::class.java)
                    .putExtra(EXTRA_IDLE, idle)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
            )
        }
    }
}
