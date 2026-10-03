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
 * Screen policy (#39), while a PC is paired, see [policyScreen]:
 * - phone connected ⇒ CarPlay;
 * - SimHub up, no phone ⇒ the idle dashboard ([IdleMode.DASHBOARD] and a URL to show), else the
 *   rigPlay idle screen;
 * - SimHub down ⇒ the rigPlay idle screen (the PC's web server is gone with it).
 *
 * The policy only acts when its answer changes (link up/lost, phone connected/gone, a new `state`, a
 * setting), and only replaces screens it owns: CarPlay and the two idle screens. The home page and a
 * dashboard opened by hand (SimHub button, `command showDashboard`) are never taken away, and a live
 * CarPlay session is never covered since a connected phone always means CarPlay. Like the phone
 * start, a change while rigPlay is in the background waits until a rigPlay screen is in the
 * foreground (Android blocks activity starts from the background; an idle screen counts); one owed
 * while the phone is being started waits until its session exists, because CarPlayHostActivity has
 * to lay out once to start it.
 *
 * Not thread-safe: call everything on the main thread.
 */
class RigSessionLifecycle(
    private val link: SimHubLinkPort,
    private val phone: PhoneSession,
    private val screens: Screens,
    private val idle: IdleInputs = IdleInputs.NONE,
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

        /** DashboardActivity in idle mode (#39). */
        fun showIdleDashboard()

        /** The built-in rigPlay idle screen, [OfflineIdleActivity] (#39). */
        fun showOfflineIdle()

        /** The rigPlay home page: an idle screen with nothing left to show (the pairing went away). */
        fun showHome()
    }

    /** What the screen policy needs besides the link and the phone (#39). */
    interface IdleInputs {
        /** A SimHub PC is paired: the policy applies. Unpaired, the screens are the user's business. */
        fun paired(): Boolean

        fun mode(): IdleMode

        /** The idle dashboard or its fallback, the main dashboard, can be loaded ([DashboardContent.resolveIdle]). */
        fun idleDashboardAvailable(): Boolean

        companion object {
            /** No pairing, hence no policy. */
            val NONE: IdleInputs = object : IdleInputs {
                override fun paired() = false
                override fun mode() = IdleMode.DEFAULT
                override fun idleDashboardAvailable() = false
            }
        }
    }

    /** What rigPlay shows in the foreground. */
    enum class Foreground { NONE, HOME, CARPLAY, DASHBOARD, IDLE_DASHBOARD, OFFLINE_IDLE }

    /** The screen the policy wants (#39). */
    enum class PolicyScreen { CARPLAY, IDLE_DASHBOARD, OFFLINE_IDLE }

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

    /** The policy's last answer; screens move only when it changes. */
    private var policyTarget: PolicyScreen? = null

    /** [policyTarget] still has to be shown (waiting for the foreground or for the phone's session). */
    private var screenOwed = false

    /** [startPhone] opened CarPlayHostActivity and its session does not exist yet. */
    private var phoneStarting = false

    /** SimHub went away and the phone is being dropped: it no longer counts as connected. */
    private var phoneStopping = false

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
        evaluate()
        publishStatus()
    }

    fun onLinkLost() {
        linkUp = false
        pendingStart = false
        phoneStarting = false
        phoneConnectionAllowed = false
        if (phone.hasSession()) {
            log("SimHub lost; dropping the phone")
            phoneStopping = true
            phone.stop {}
        }
        evaluate()
        publishStatus()
    }

    /** The pairing was removed: no coupling any more, the phone is the user's business. */
    fun onUnpaired() {
        linkUp = false
        pendingStart = false
        phoneStarting = false
        phoneConnectionAllowed = true
        evaluate()
    }

    /** A new `state` (dashboard URLs, web server) arrived or an idle setting changed (#39). */
    fun onIdleInputsChanged() = evaluate()

    /**
     * The user pressed Connect phone / Connect with USB. Always allowed; returns true when the user
     * should be warned that SimHub is down (audio then stays on the tablet).
     */
    fun onManualConnect(paired: Boolean): Boolean {
        phoneConnectionAllowed = true
        return paired && !linkUp
    }

    fun onPhoneChanged() {
        if (phoneStarting && (phone.hasSession() || phone.isConnected())) phoneStarting = false
        if (phoneStopping && !phone.hasSession()) phoneStopping = false
        evaluate()
        publishStatus()
    }

    fun onForegroundChanged(next: Foreground) {
        if (foreground == next) return
        // The user left CarPlayHostActivity before it could start the phone (permission prompt, Home).
        if (phoneStarting && foreground == Foreground.CARPLAY && next != Foreground.NONE) phoneStarting = false
        foreground = next
        if (pendingStart && next != Foreground.NONE) {
            pendingStart = false
            if (linkUp && !phone.hasSession() && phone.autoStartBlocker() == null) startPhone()
        }
        settle()
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

    /**
     * What the tablet should show by itself now (#39); `null` when no PC is paired and no phone is
     * connected: nothing.
     */
    fun policyScreen(): PolicyScreen? = when {
        phone.isConnected() && !phoneStopping -> PolicyScreen.CARPLAY
        !idle.paired() -> null
        linkUp && idle.mode() == IdleMode.DASHBOARD && idle.idleDashboardAvailable() -> PolicyScreen.IDLE_DASHBOARD
        else -> PolicyScreen.OFFLINE_IDLE
    }

    /**
     * Shows the policy's screen now, whatever is in the foreground: the dashboard's close button,
     * rigPlay opening with automatic connection on, the home page's idle screen button. Call it from
     * a foreground activity. Returns false, showing nothing, when the policy has no screen.
     */
    fun showPolicyScreen(): Boolean {
        val target = policyScreen() ?: return false
        policyTarget = target
        screenOwed = false
        show(target)
        return true
    }

    /** The snapshot the plugin should see now. */
    fun currentStatus(): SimHubMessage.Status {
        val connected = phone.isConnected()
        val screen = when (foreground) {
            Foreground.DASHBOARD -> Screen.DASHBOARD
            Foreground.CARPLAY -> if (connected) Screen.CARPLAY else Screen.IDLE
            Foreground.HOME, Foreground.IDLE_DASHBOARD, Foreground.OFFLINE_IDLE -> Screen.IDLE
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
        phoneStarting = true
        // Once the session exists, the idle screen covers CarPlayHostActivity's "waiting for the iPhone".
        screenOwed = true
        phone.start()
    }

    private fun evaluate() {
        val next = policyScreen()
        if (next != policyTarget) {
            log("screen policy: ${policyTarget ?: "none"} -> ${next ?: "none"}")
            policyTarget = next
            screenOwed = true
        }
        settle()
    }

    /** Shows the owed screen once it can be; drops it when the user is on a screen the policy does not own. */
    private fun settle() {
        if (!screenOwed || phoneStarting || foreground == Foreground.NONE) return
        screenOwed = false
        if (foreground !in POLICY_OWNED) return
        val target = policyTarget
        if (target != null) {
            show(target)
        } else if (foreground == Foreground.IDLE_DASHBOARD || foreground == Foreground.OFFLINE_IDLE) {
            log("screen policy: no PC paired; leaving the idle screen")
            screens.showHome()
        }
    }

    private fun show(target: PolicyScreen) {
        when (target) {
            PolicyScreen.CARPLAY -> if (foreground != Foreground.CARPLAY) screens.showCarPlay()
            PolicyScreen.IDLE_DASHBOARD -> if (foreground != Foreground.IDLE_DASHBOARD) screens.showIdleDashboard()
            PolicyScreen.OFFLINE_IDLE -> if (foreground != Foreground.OFFLINE_IDLE) screens.showOfflineIdle()
        }
    }

    private companion object {
        /** Screens the policy may replace by itself; HOME and a hand-opened DASHBOARD stay. */
        val POLICY_OWNED = setOf(Foreground.CARPLAY, Foreground.IDLE_DASHBOARD, Foreground.OFFLINE_IDLE)
    }
}
