package com.shilapi.xcertplay

import com.shilapi.xcertplay.RigSessionLifecycle.Foreground
import com.shilapi.xcertplay.simhub.MediaAction
import com.shilapi.xcertplay.simhub.NowPlaying
import com.shilapi.xcertplay.simhub.Screen
import com.shilapi.xcertplay.simhub.SimHubCommand
import com.shilapi.xcertplay.simhub.SimHubMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RigSessionLifecycleTest {
    private class FakePhone : RigSessionLifecycle.PhoneSession {
        var session = false
        var connected = false
        var blocker: String? = null
        var starts = 0
        var stops = 0
        override fun hasSession() = session
        override fun isConnected() = connected
        override fun phoneName(): String? = "Tim's iPhone"
        override fun autoStartBlocker() = blocker
        override fun start() { starts++; session = true }
        override fun stop(completion: () -> Unit) {
            stops++; session = false; connected = false; completion()
        }
    }

    private class FakeScreens : RigSessionLifecycle.Screens {
        var dashboards = 0
        var carPlays = 0
        override fun showDashboard() { dashboards++ }
        override fun showCarPlay() { carPlays++ }
    }

    private val link = FakeSimHubLinkPort()
    private val phone = FakePhone()
    private val screens = FakeScreens()
    private val lifecycle = RigSessionLifecycle(link, phone, screens)

    private val song = NowPlaying("Teardrop", "Massive Attack", "Mezzanine", "Music", true, 83.4, 330.0, 1L)

    @Test fun linkUpStartsThePhoneWhenRigPlayIsOnScreen() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        lifecycle.onLinkUp()
        assertEquals(1, phone.starts)
        assertTrue(lifecycle.linkUp)
    }

    @Test fun linkUpInBackgroundDefersTheStartUntilAScreenIsShown() {
        lifecycle.onLinkUp()
        assertEquals(0, phone.starts)
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(1, phone.starts)
        lifecycle.onForegroundChanged(Foreground.NONE)
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(1, phone.starts)
    }

    @Test fun deferredStartIsCancelledWhenTheLinkDropsFirst() {
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(0, phone.starts)
    }

    @Test fun linkUpDoesNotRestartARunningSession() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        phone.session = true
        lifecycle.onLinkUp()
        assertEquals(0, phone.starts)
    }

    @Test fun linkUpRespectsBlockers() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        phone.blocker = "no iPhone chosen"
        lifecycle.onLinkUp()
        assertEquals(0, phone.starts)
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun linkLostDropsThePhoneAndBlocksReconnects() {
        lifecycle.onForegroundChanged(Foreground.CARPLAY)
        lifecycle.onLinkUp()
        phone.connected = true
        lifecycle.onLinkLost()
        assertEquals(1, phone.stops)
        assertFalse(phone.session)
        assertFalse(lifecycle.phoneConnectionAllowed)
        assertFalse(lifecycle.linkUp)
    }

    @Test fun linkBackRestartsThePhone() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        lifecycle.onLinkUp()
        assertEquals(2, phone.starts)
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun manualConnectWhileSimHubIsDownIsAllowedWithAWarning() {
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        assertTrue(lifecycle.onManualConnect(paired = true))
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun manualConnectNeedsNoWarningWhenUpOrUnpaired() {
        assertFalse(lifecycle.onManualConnect(paired = false))
        lifecycle.onLinkUp()
        assertFalse(lifecycle.onManualConnect(paired = true))
    }

    @Test fun unpairingLiftsTheGuard() {
        lifecycle.onLinkUp()
        lifecycle.onLinkLost()
        lifecycle.onUnpaired()
        assertTrue(lifecycle.phoneConnectionAllowed)
    }

    @Test fun statusFollowsPhoneAndScreen() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        assertEquals(SimHubMessage.Status(false, null, Screen.IDLE, null), link.statuses.last())
        phone.session = true; phone.connected = true
        lifecycle.onForegroundChanged(Foreground.CARPLAY)
        assertEquals(SimHubMessage.Status(true, "Tim's iPhone", Screen.CARPLAY, null), link.statuses.last())
        lifecycle.onForegroundChanged(Foreground.DASHBOARD)
        assertEquals(Screen.DASHBOARD, link.statuses.last().screen)
        lifecycle.onForegroundChanged(Foreground.NONE)
        assertEquals(Screen.OFF, link.statuses.last().screen)
    }

    @Test fun projectionWithoutAPhoneIsIdle() {
        lifecycle.onForegroundChanged(Foreground.CARPLAY)
        assertEquals(Screen.IDLE, link.statuses.last().screen)
    }

    @Test fun unchangedStatusIsNotResent() {
        lifecycle.onForegroundChanged(Foreground.HOME)
        val count = link.statuses.size
        lifecycle.onPhoneChanged()
        lifecycle.publishStatus()
        assertEquals(count, link.statuses.size)
        lifecycle.publishStatus(force = true)
        assertEquals(count + 1, link.statuses.size)
    }

    @Test fun nowPlayingIsSentOnlyWithAPhone() {
        lifecycle.updateNowPlaying(song)
        assertNull(link.statuses.last().nowPlaying)
        phone.connected = true
        lifecycle.onPhoneChanged()
        assertEquals(song, link.statuses.last().nowPlaying)
        lifecycle.updateNowPlaying(song.copy(position = 120.0))
        assertEquals(120.0, link.statuses.last().nowPlaying!!.position, 0.0)
    }

    @Test fun showCommandsOpenTheScreens() {
        lifecycle.onCommand(SimHubCommand.ShowDashboard)
        assertEquals(1, screens.dashboards)
        phone.session = true
        lifecycle.onCommand(SimHubCommand.ShowCarPlay)
        assertEquals(1, screens.carPlays)
    }

    @Test fun showCarPlayWithoutAPhoneIsUnavailable() {
        lifecycle.onCommand(SimHubCommand.ShowCarPlay)
        assertEquals(0, screens.carPlays)
        assertEquals(1, link.unavailable.size)
    }

    @Test fun mediaCommandsGoToTheHandlerOrAreUnavailable() {
        lifecycle.onCommand(SimHubCommand.Media(MediaAction.NEXT))
        assertEquals(1, link.unavailable.size)
        val received = mutableListOf<SimHubCommand.Media>()
        lifecycle.mediaCommandHandler = { received += it }
        lifecycle.onCommand(SimHubCommand.Media(MediaAction.PLAY_PAUSE))
        assertEquals(listOf(SimHubCommand.Media(MediaAction.PLAY_PAUSE)), received)
        assertEquals(1, link.unavailable.size)
    }
}
