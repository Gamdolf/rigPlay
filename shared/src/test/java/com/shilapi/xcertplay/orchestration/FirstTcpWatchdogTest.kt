package com.shilapi.xcertplay.orchestration

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class FirstTcpWatchdogTest {
    private class FakeTimer {
        val pending = mutableListOf<Pair<Long, () -> Unit>>()
        var cancels = 0
        fun schedule(delay: Long, action: () -> Unit): () -> Unit {
            val entry = delay to action
            pending += entry
            return { if (pending.remove(entry)) cancels++ }
        }
        fun fire() = pending.toList().also { pending.clear() }.forEach { it.second() }
    }

    private val timer = FakeTimer()
    private var timeouts = 0
    private val logs = mutableListOf<String>()
    private val watchdog = FirstTcpWatchdog(timer::schedule, { timeouts++ }, timeoutMillis = 30_000, log = logs::add)

    @Test fun nothingIsArmedBeforeStartSession() {
        watchdog.accepted(external = false)
        assertTrue(timer.pending.isEmpty())
        assertEquals(0, timeouts)
    }

    @Test fun noConnectionAfterStartSessionTimesOutOnce() {
        watchdog.startSessionSent()
        watchdog.startSessionSent()
        assertEquals(listOf(30_000L), timer.pending.map { it.first })
        timer.fire()
        assertEquals(1, timeouts)
        watchdog.accepted(external = true)
        timer.fire()
        assertEquals(1, timeouts)
        assertTrue(logs.any { it.startsWith("first TCP timeout") })
    }

    @Test fun theIphoneConnectingCancelsTheDeadline() {
        watchdog.startSessionSent()
        watchdog.accepted(external = true)
        assertEquals(1, timer.cancels)
        timer.fire()
        assertEquals(0, timeouts)
    }

    @Test fun theTabletsOwnProbeDoesNotCountAsTheIphone() {
        watchdog.startSessionSent()
        watchdog.accepted(external = false)
        assertEquals(0, timer.cancels)
        timer.fire()
        assertEquals(1, timeouts)
    }

    @Test fun aConnectionBeforeStartSessionMeansNoDeadline() {
        watchdog.accepted(external = true)
        watchdog.startSessionSent()
        assertTrue(timer.pending.isEmpty())
    }

    @Test fun terminateCancelsAndAStaleTimerDoesNothing() {
        watchdog.startSessionSent()
        val stale = timer.pending.single().second
        watchdog.terminate()
        assertEquals(1, timer.cancels)
        stale()
        assertEquals(0, timeouts)
    }
}
