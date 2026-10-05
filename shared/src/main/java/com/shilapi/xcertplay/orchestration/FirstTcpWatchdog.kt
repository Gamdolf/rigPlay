package com.shilapi.xcertplay.orchestration

/**
 * Wireless startup can hang after StartSession: the iPhone has the network details but never opens
 * the AirPlay TCP connection, and nothing else fails. Armed when StartSession is first sent, this
 * calls [onTimeout] once if no connection from the iPhone arrives within [timeoutMillis], so the
 * normal failure and reconnect path takes over. Connections the tablet opens to itself (Bonjour
 * probes) do not count; see [com.shilapi.xcertplay.airplay.AirPlaySessionListener.onTcpAccepted].
 */
internal class FirstTcpWatchdog(
    /** Runs the action after the delay and returns a cancel function. */
    private val schedule: (Long, () -> Unit) -> (() -> Unit),
    private val onTimeout: () -> Unit,
    private val timeoutMillis: Long = TIMEOUT_MILLIS,
    private val log: (String) -> Unit = {},
) {
    private var armed = false
    private var finished = false
    private var cancelTimer: (() -> Unit)? = null

    /** StartSession went out; only the first one arms the deadline. */
    @Synchronized fun startSessionSent() {
        if (finished || armed) return
        armed = true
        log("first TCP deadline armed timeoutMs=$timeoutMillis")
        cancelTimer = schedule(timeoutMillis, ::expire)
    }

    /** A TCP connection reached the AirPlay listener; [external] is false for the tablet's own probes. */
    @Synchronized fun accepted(external: Boolean) {
        if (finished || !external) return
        finished = true
        if (armed) log("first TCP accepted")
        cancel()
    }

    /** The run ended for another reason (teardown, failure, restart). */
    @Synchronized fun terminate() {
        finished = true
        cancel()
    }

    private fun expire() {
        synchronized(this) {
            if (finished) return
            finished = true
            cancelTimer = null
            log("first TCP timeout: no AirPlay connection from the iPhone ${timeoutMillis}ms after StartSession")
        }
        onTimeout()
    }

    private fun cancel() {
        cancelTimer?.invoke()
        cancelTimer = null
    }

    companion object {
        const val TIMEOUT_MILLIS = 30_000L
    }
}
