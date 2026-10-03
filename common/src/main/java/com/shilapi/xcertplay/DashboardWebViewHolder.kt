package com.shilapi.xcertplay

/**
 * The one dashboard page of the process (#51): loaded as soon as SimHub names a dashboard and kept
 * loaded while the dashboard screen is closed, so opening it is instant. [RigPlayApplication] owns it
 * and feeds it every link state ([update]); [DashboardActivity] only shows its surface ([attach]).
 *
 * Decisions, all on the main thread:
 * - Load: a dashboard URL that differs from the one loaded, or the same one after a failure, a
 *   blank or a release, is loaded; the same URL is left alone. Behind NAT the URL as given may not
 *   answer within the probe: the same URL on the connected host is loaded instead (#30).
 * - Keep: while the link is down ([DashboardContent.Disconnected]) the page stays as it is, its own
 *   websocket reconnects when SimHub is back. A server turned off, no dashboard selected or no
 *   pairing blank the page, so the next dashboard loads fresh.
 * - Release: the surface is destroyed once the link has been down for [RELEASE_AFTER_MS], or on
 *   memory pressure while no screen shows it ([onTrimMemory]); the next dashboard re-creates it.
 * - Hidden: [Surface.setVisible] false only marks the page hidden. SimHub's page throttles to the
 *   acknowledged frames and keeps its websocket open while hidden; freezing its JavaScript (as
 *   WebView.pauseTimers would) closes the socket, so the timers keep running (see the surface).
 */
class DashboardWebViewHolder<S : DashboardWebViewHolder.Surface>(
    private val createSurface: () -> S,
    private val scheduler: Scheduler,
    private val prober: Prober,
    private val log: (String) -> Unit = {},
) {
    /** The page itself (a WebView on Android). */
    interface Surface {
        /** Loads [url] as a new document, even when only its fragment differs from the current one. */
        fun load(url: String)

        /** Stops loading and shows an empty page. */
        fun clear()

        /** On screen or not; never stops the page's timers or websocket. */
        fun setVisible(visible: Boolean)

        /** Frees the page; the surface is not used again. */
        fun destroy()
    }

    fun interface Cancellable {
        fun cancel()
    }

    /** Runs a task on the main thread after a delay. */
    fun interface Scheduler {
        fun schedule(delayMs: Long, task: () -> Unit): Cancellable
    }

    /** Checks that [host]:[port] accepts a TCP connection; [result] arrives on the main thread. */
    fun interface Prober {
        fun probe(host: String, port: Int, result: (Boolean) -> Unit)
    }

    /** What the surface holds. */
    sealed class Page {
        object Idle : Page() {
            override fun toString() = "Idle"
        }

        data class Loading(val url: String) : Page()
        data class Loaded(val url: String) : Page()
        data class Failed(val url: String, val description: String) : Page()
    }

    /** One load of a dashboard URL, with the connected-host fallback for NAT (#30). */
    private data class Attempt(val content: DashboardContent.Load, val usingFallback: Boolean, val serial: Int) {
        val url: String get() = if (usingFallback) content.fallbackUrl!! else content.url
    }

    /** The surface, once a dashboard was loaded; `null` before and after a release. */
    var surface: S? = null
        private set

    /** The latest content from [update]. */
    var content: DashboardContent? = null
        private set

    var page: Page = Page.Idle
        private set

    /** Told about every change of [page], [content] or [surface] (the attached screen re-renders). */
    var listener: (() -> Unit)? = null

    /** The screen showing the surface, if any. */
    var owner: Any? = null
        private set
    val attached: Boolean get() = owner != null
    var visible: Boolean = false
        private set

    private var attempt: Attempt? = null
    private var serial = 0
    private var failedSerial = -1
    private var releaseTask: Cancellable? = null

    /** A new link state (any change; repeated contents are free). */
    fun update(next: DashboardContent) {
        content = next
        when (next) {
            is DashboardContent.Load -> {
                cancelRelease()
                val current = attempt
                // The same dashboard, loading or loaded: keep it (#51).
                if (current == null || current.content != next || page is Page.Failed || surface == null) {
                    start(Attempt(next, usingFallback = false, serial = ++serial))
                }
            }
            DashboardContent.Disconnected -> scheduleRelease()
            DashboardContent.NotPaired -> {
                blank()
                scheduleRelease()
            }
            DashboardContent.ServerOff, DashboardContent.NoDashboard -> {
                cancelRelease()
                blank()
            }
        }
        notifyListener()
    }

    /** The screen's Retry: loads the current dashboard again. */
    fun retry() {
        attempt = null
        content?.let(::update)
    }

    /** [owner] shows the surface from now on (creating it if a dashboard is due); returns it. */
    fun attach(owner: Any): S? {
        if (this.owner !== owner) {
            this.owner = owner
            log("dashboard attached to $owner")
        }
        // Released (memory, link down) while closed: the dashboard comes back now.
        if (surface == null) content?.let(::update)
        return surface
    }

    /** Only the current owner's calls count: a screen being destroyed must not hide its successor. */
    fun setVisible(owner: Any, visible: Boolean) {
        if (this.owner !== owner || this.visible == visible) return
        this.visible = visible
        surface?.setVisible(visible)
    }

    /** [owner] no longer shows the surface; the page stays loaded. */
    fun detach(owner: Any) {
        if (this.owner !== owner) return
        setVisible(owner, false)
        this.owner = null
        listener = null
        log("dashboard detached from $owner")
    }

    /** From the WebView: the main frame of [url] finished loading. */
    fun onPageFinished(url: String) {
        val current = attempt ?: return
        if (url == BLANK || failedSerial == current.serial || page is Page.Loaded) return
        // A failed URL as given still reports onPageFinished after the switch to the fallback.
        if (DashboardUrls.endpoint(url)?.first != DashboardUrls.endpoint(current.url)?.first) return
        page = Page.Loaded(current.url)
        log("dashboard loaded from ${if (current.usingFallback) "the connected host" else "the URL as given"}: ${current.url}")
        notifyListener()
    }

    /** From the WebView: the main frame of [url] failed. */
    fun onMainFrameError(url: String, description: String) {
        val current = attempt ?: return
        // Ignore the abandoned URL as given once the fallback is loading.
        if (DashboardUrls.endpoint(url)?.first != DashboardUrls.endpoint(current.url)?.first) return
        failedSerial = current.serial
        if (!current.usingFallback && current.content.fallbackUrl != null) {
            useFallback(current, description)
            return
        }
        log("dashboard ${current.url} failed: $description")
        page = Page.Failed(current.url, description)
        notifyListener()
    }

    /** `ComponentCallbacks2.onTrimMemory`: frees the page under pressure unless a screen shows it. */
    fun onTrimMemory(level: Int) {
        if (shouldReleaseOnTrim(level) && !attached) release("memory (trim level $level)")
    }

    /** Destroys the surface; the next dashboard load creates a new one. */
    fun release(reason: String) {
        cancelRelease()
        val released = surface ?: return
        log("dashboard page released: $reason")
        surface = null
        attempt = null
        page = Page.Idle
        released.destroy()
        notifyListener()
    }

    private fun start(next: Attempt) {
        attempt = next
        page = Page.Loading(next.url)
        log("loading dashboard ${next.url}${if (next.usingFallback) " (connected host)" else ""}${if (attached) "" else " in the background"}")
        val target = surface ?: createSurface().also {
            surface = it
            it.setVisible(visible)
        }
        target.load(next.url)
        if (!next.usingFallback && next.content.fallbackUrl != null) probe(next)
    }

    /**
     * The URL's host differs from the one the link uses (NAT): if it does not accept a TCP
     * connection, switch to the connected host before the WebView gives up.
     */
    private fun probe(current: Attempt) {
        val (host, port) = DashboardUrls.endpoint(current.url) ?: return
        prober.probe(host, port) { reachable ->
            if (!reachable) useFallback(current, "host $host:$port not reachable")
        }
    }

    private fun useFallback(failed: Attempt, reason: String) {
        if (attempt != failed || failed.usingFallback || page is Page.Loaded) return
        log("dashboard URL as given failed ($reason); retrying on the connected host")
        start(failed.copy(usingFallback = true, serial = ++serial))
        notifyListener()
    }

    private fun blank() {
        if (attempt == null && page == Page.Idle) return
        attempt = null
        page = Page.Idle
        surface?.clear()
    }

    private fun scheduleRelease() {
        if (releaseTask != null || surface == null) return
        releaseTask = scheduler.schedule(RELEASE_AFTER_MS) {
            releaseTask = null
            release("SimHub link down for ${RELEASE_AFTER_MS / 60_000} minutes")
        }
    }

    private fun cancelRelease() {
        releaseTask?.cancel()
        releaseTask = null
    }

    private fun notifyListener() = listener?.invoke()

    companion object {
        const val RELEASE_AFTER_MS = 10 * 60_000L
        const val BLANK = "about:blank"

        // ComponentCallbacks2 levels, kept here so the decision stays a plain function.
        private const val TRIM_MEMORY_RUNNING_CRITICAL = 15
        private const val TRIM_MEMORY_COMPLETE = 80

        /** Critical while running, or the process is next in line to be killed. */
        fun shouldReleaseOnTrim(level: Int): Boolean =
            level == TRIM_MEMORY_RUNNING_CRITICAL || level >= TRIM_MEMORY_COMPLETE
    }
}
