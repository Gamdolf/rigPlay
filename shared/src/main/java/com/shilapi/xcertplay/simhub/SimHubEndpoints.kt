package com.shilapi.xcertplay.simhub

/**
 * Process-wide plug points between the CarPlay session and whoever owns the [SimHubLink] (the rig
 * session coordinator). The owner sets [audioTransport] and [statusSink] when it creates the link,
 * clears them when it stops the link, and forwards `command media` to [mediaBridge]:
 *
 * ```
 * SimHubEndpoints.audioTransport = SimHubLinkAudioTransport(link)
 * SimHubEndpoints.statusSink = SimHubLinkStatus(link)
 * // in SimHubLink.Listener.onCommand:
 * if (SimHubEndpoints.mediaBridge.onCommand(command) == SimHubMediaBridge.Result.UNAVAILABLE) {
 *     link.sendCommandUnavailable()
 * }
 * ```
 */
object SimHubEndpoints {
    @Volatile var audioTransport: SimHubAudioTransport? = null

    /** Setting it sends the current phone and now-playing state to the new sink. */
    @Volatile var statusSink: SimHubStatusSink? = null
        set(value) {
            field = value
            mediaBridge.republish()
        }

    /** Bound to the running CarPlay session by `CarPlayMediaKeys.attach`/`detach`. */
    val mediaBridge: SimHubMediaBridge = SimHubMediaBridge(statusSink = { statusSink })
}
