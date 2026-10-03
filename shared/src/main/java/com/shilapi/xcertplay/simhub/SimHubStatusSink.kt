package com.shilapi.xcertplay.simhub

/**
 * Receives what the CarPlay session reports for `status` (`docs/protocol.md` §6.7).
 * [SimHubLinkStatus] merges it into the status the link sends; the owner of the link plugs one into
 * [SimHubEndpoints.statusSink]. Calls may come from any thread.
 */
interface SimHubStatusSink {
    /** The latest `status.nowPlaying`; `null` when nothing is known (no phone, no media). */
    fun updateNowPlaying(nowPlaying: NowPlaying?)

    /** `status.phoneConnected` and `status.phoneName`. */
    fun updatePhone(connected: Boolean, phoneName: String?) {}
}

/**
 * Keeps the tablet's whole `status` (§6.7) and hands every change to [SimHubLink.send], which
 * coalesces bursts to one line per 250 ms (trailing edge). CarPlay feeds [updateNowPlaying] and
 * [updatePhone] through [SimHubMediaBridge]; the owner of the link feeds [updateScreen].
 */
class SimHubLinkStatus(private val link: SimHubLink) : SimHubStatusSink {
    private var status: SimHubMessage.Status = SimHubMessage.Status.IDLE

    val current: SimHubMessage.Status get() = synchronized(this) { status }

    override fun updateNowPlaying(nowPlaying: NowPlaying?) = update { it.copy(nowPlaying = nowPlaying) }

    override fun updatePhone(connected: Boolean, phoneName: String?) = update {
        it.copy(
            phoneConnected = connected,
            phoneName = if (connected) phoneName else null,
            nowPlaying = if (connected) it.nowPlaying else null,
        )
    }

    fun updateScreen(screen: Screen) = update { it.copy(screen = screen) }

    private inline fun update(change: (SimHubMessage.Status) -> SimHubMessage.Status) {
        synchronized(this) {
            val next = change(status)
            if (next == status) return
            status = next
            link.send(next)
        }
    }
}
