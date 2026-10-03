package com.shilapi.xcertplay.simhub

/**
 * Process-wide plug points between the CarPlay session and whoever owns the [SimHubLink] (the rig
 * session coordinator). The owner sets [audioTransport] when it creates the link and clears it when it
 * stops the link:
 *
 * ```
 * SimHubEndpoints.audioTransport = SimHubLinkAudioTransport(link)
 * ```
 */
object SimHubEndpoints {
    @Volatile var audioTransport: SimHubAudioTransport? = null
}
