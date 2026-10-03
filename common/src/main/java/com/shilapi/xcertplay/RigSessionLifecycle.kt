package com.shilapi.xcertplay

import com.shilapi.xcertplay.simhub.NavStatus
import com.shilapi.xcertplay.simhub.NowPlaying
import com.shilapi.xcertplay.simhub.Screen
import com.shilapi.xcertplay.simhub.SimHubCommand
import com.shilapi.xcertplay.simhub.SimHubMessage

/**
 * Couples the phone to the PC (#29): the iPhone is connected exactly while the paired SimHub PC is
 * reachable, and the plugin always knows what the tablet shows (`status`, §6.7).
 *
 * - link up ⇒ start CarPlay the way "Connect phone" does (deferred until a rigPlay screen is in the
 *   foreground, since Android blocks activity starts from the background);
 * - link lost ⇒ stop the session (AirPlay, Bonjour, Wi-Fi Direct group, Bluetooth link go down, so the
 *   iPhone drops CarPlay) and refuse reconnects until the link is back or the user connects by hand;
 * - every change of phone, screen, now-playing or route guidance ⇒ a new `status` (SimHubLink
 *   coalesces to 250 ms).
 *
 * Not thread-safe: call everything on the main thread.
 */
class RigSessionLifecycle(
    private val link: SimHubLinkPort,
    private val phone: PhoneSession,
    private val screens: Screens,
    private val log: (String) -> Unit = {},
) {
    /** The CarPlay session as the coordinator sees it. */
    interface PhoneSession {
        /** A session exists (starting, running or stopping). */
        fun hasSession(): Boolean

        /** The iPhone's AirPlay session is up. */
        fun isConnected(): Boolean

        fun phoneName(): String?

        /** Why the phone cannot be started without the user (no iPhone chosen, setup error…), or `null`. */
        fun autoStartBlocker(): String?

        /** Starts CarPlay like the "Connect phone" button. */
        fun start()

        /** Stops the session like the "Disconnect" button. */
        fun stop(completion: () -> Unit)
    }

    interface Screens {
        fun showDashboard()
        fun showCarPlay()
    }

    /** What rigPlay shows in the foreground. */
    enum class Foreground { NONE, HOME, CARPLAY, DASHBOARD }

    /** True while the paired PC's link is up. */
    var linkUp = false
        private set

    /** False after SimHub dropped the phone: CarPlayHostActivity must not reconnect or start on its own. */
    var phoneConnectionAllowed = true
        private set

    var foreground = Foreground.NONE
        private set

    var nowPlaying: NowPlaying? = null
        private set

    /** CarPlay's next maneuver for `status.nav` (#47); `null` without route guidance. */
    var nav: NavStatus? = null
        private set

    /** `command media` goes here (#32). Without a handler the plugin gets `commandUnavailable`. */
    var mediaCommandHandler: ((SimHubCommand.Media) -> Unit)? = null

    private var pendingStart = false
    private var lastStatus: SimHubMessage.Status? = null

    fun onLinkUp() {
        linkUp = true
        phoneConnectionAllowed = true
        if (phone.hasSession()) {
            log("SimHub up; phone session already running")
        } else {
            val blocker = phone.autoStartBlocker()
            when {
                blocker != null -> log("SimHub up; not starting the phone: $blocker")
                foreground == Foreground.NONE -> {
                    log("SimHub up; starting the phone when rigPlay is in the foreground")
                    pendingStart = true
                }
                else -> startPhone()
            }
        }
        publishStatus()
    }

    fun onLinkLost() {
        linkUp = false
        pendingStart = false
        phoneConnectionAllowed = false
        if (phone.hasSession()) {
            log("SimHub lost; dropping the phone")
            phone.stop {}
        }
        publishStatus()
    }

    /** The pairing was removed: no coupling any more, the phone is the user's business. */
    fun onUnpaired() {
        linkUp = false
        pendingStart = false
        phoneConnectionAllowed = true
    }

    /**
     * The user pressed Connect phone / Connect with USB. Always allowed; returns true when the user
     * should be warned that SimHub is down (audio then stays on the tablet).
     */
    fun onManualConnect(paired: Boolean): Boolean {
        phoneConnectionAllowed = true
        return paired && !linkUp
    }

    fun onPhoneChanged() = publishStatus()

    fun onForegroundChanged(next: Foreground) {
        if (foreground == next) return
        foreground = next
        if (pendingStart && next != Foreground.NONE) {
            pendingStart = false
            if (linkUp && !phone.hasSession() && phone.autoStartBlocker() == null) startPhone()
        }
        publishStatus()
    }

    fun updateNowPlaying(value: NowPlaying?) {
        nowPlaying = value
        publishStatus()
    }

    fun updateNav(value: NavStatus?) {
        nav = value
        publishStatus()
    }

    fun onCommand(command: SimHubCommand) {
        when (command) {
            SimHubCommand.ShowDashboard -> screens.showDashboard()
            SimHubCommand.ShowCarPlay ->
                if (phone.hasSession()) screens.showCarPlay() else link.sendCommandUnavailable("no phone connected")
            is SimHubCommand.Media -> {
                val handler = mediaCommandHandler
                if (handler == null) link.sendCommandUnavailable("media commands are not available") else handler(command)
            }
        }
    }

    /** The snapshot the plugin should see now. */
    fun currentStatus(): SimHubMessage.Status {
        val connected = phone.isConnected()
        val screen = when (foreground) {
            Foreground.DASHBOARD -> Screen.DASHBOARD
            Foreground.CARPLAY -> if (connected) Screen.CARPLAY else Screen.IDLE
            Foreground.HOME -> Screen.IDLE
            Foreground.NONE -> Screen.OFF
        }
        return SimHubMessage.Status(
            phoneConnected = connected,
            phoneName = if (connected) phone.phoneName() else null,
            screen = screen,
            nowPlaying = if (connected) nowPlaying else null,
            nav = if (connected) nav else null,
        )
    }

    /** Sends the snapshot when it changed, or always with [force]. Safe while the link is down (sent after pairing). */
    fun publishStatus(force: Boolean = false) {
        val next = currentStatus()
        if (!force && next == lastStatus) return
        lastStatus = next
        link.send(next)
    }

    private fun startPhone() {
        log("SimHub up; starting the phone")
        phone.start()
    }
}
