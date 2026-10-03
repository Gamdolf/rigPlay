package com.shilapi.xcertplay.simhub

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.Random
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [29], manifest = Config.NONE)
class SimHubLinkAudioTransportTest {
    private lateinit var server: FakeSimHubServer
    private lateinit var receiver: DatagramSocket
    private var link: SimHubLink? = null
    private var transport: SimHubLinkAudioTransport? = null

    @Before fun setUp() {
        receiver = DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), 0)).apply { soTimeout = 3_000 }
        server = FakeSimHubServer().start()
        server.stateAfterPairing = server.stateAfterPairing.copy(
            audio = AudioSettings(enabled = true, port = receiver.localPort, formats = listOf(SimHubProtocol.FORMAT_PCM_S16LE)),
        )
    }

    @After fun tearDown() {
        transport?.close()
        link?.stop()
        server.stop()
        receiver.close()
    }

    private fun pairedLink(onUp: CountDownLatch): SimHubLink {
        val listener = object : SimHubLink.Listener {
            override fun onLinkUp(state: SimHubState) = onUp.countDown()
        }
        return SimHubLink(IDENTITY, listener, timing = FAST, random = Random(1), log = {}).also {
            link = it
            transport = SimHubLinkAudioTransport(it)
            it.start(SimHubLink.Target("127.0.0.1", server.port, FakeSimHubServer.HOST_ID, FakeSimHubServer.TOKEN))
        }
    }

    private fun awaitTarget(transport: SimHubAudioTransport): InetSocketAddress {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3)
        while (System.nanoTime() < deadline) {
            transport.audioTarget?.let { return it }
            Thread.sleep(10)
        }
        throw AssertionError("no audio target")
    }

    @Test fun datagramsGoToTheAudioPortOfThePairedHost() {
        val up = CountDownLatch(1)
        val link = pairedLink(up)
        val transport = transport!!
        assertTrue(up.await(3, TimeUnit.SECONDS))
        val target = awaitTarget(transport)
        assertEquals(receiver.localPort, target.port)
        assertEquals(1L, transport.audioEpoch)

        assertTrue(transport.audioStart(AudioStream.MEDIA, 48_000, 2))
        assertEquals(SimHubMessage.AudioStart(AudioStream.MEDIA, AudioFormat.PCM_S16LE, 48_000, 2), server.await<SimHubMessage.AudioStart>())

        val header = AudioHeader(seq = 0, stream = AudioStream.MEDIA, start = true, timestamp = 0, sampleRateHz = 48_000, channels = 2)
        val datagram = SimHubAudioCodec.encode(header, ByteArray(960) { it.toByte() })
        val padded = datagram + ByteArray(8)
        assertTrue(transport.sendDatagram(padded, datagram.size))
        val packet = DatagramPacket(ByteArray(2048), 2048)
        receiver.receive(packet)
        val received = SimHubAudioCodec.decode(packet.data, 0, packet.length)
        assertNotNull(received)
        assertEquals(header, received!!.header)
        assertEquals(960, received.payload.size)

        assertTrue(transport.audioStop(AudioStream.MEDIA))
        assertEquals(SimHubMessage.AudioStop(AudioStream.MEDIA), server.await<SimHubMessage.AudioStop>())
        assertEquals(1L, link.pairedSessions)
    }

    @Test fun noTargetWhenAudioIsDisabledOrTheLinkIsDown() {
        val up = CountDownLatch(1)
        val link = pairedLink(up)
        val transport = transport!!
        assertNull(transport.audioTarget)
        assertTrue(up.await(3, TimeUnit.SECONDS))
        awaitTarget(transport)

        server.send(server.stateAfterPairing.copy(audio = AudioSettings(enabled = false, port = receiver.localPort, formats = listOf("pcm_s16le"))))
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3)
        while (transport.audioTarget != null && System.nanoTime() < deadline) Thread.sleep(10)
        assertNull(transport.audioTarget)
        assertFalse(transport.sendDatagram(ByteArray(16), 16))

        link.stop()
        assertNull(transport.audioTarget)
    }

    @Test fun aReconnectStartsANewEpoch() {
        val up = CountDownLatch(2)
        pairedLink(up)
        val transport = transport!!
        awaitTarget(transport)
        val first = transport.audioEpoch
        server.closeConnections()
        assertTrue(up.await(5, TimeUnit.SECONDS))
        awaitTarget(transport)
        assertEquals(first + 1, transport.audioEpoch)
    }

    private companion object {
        val IDENTITY = SimHubLink.Identity(tabletId = "tablet-audio-test", name = "Tab", appVersion = "0.3.0")
        val FAST = SimHubLink.Timing(
            heartbeatIntervalMs = 100,
            linkLossTimeoutMs = 600,
            reconnectInitialDelayMs = 100,
            reconnectMaxDelayMs = 400,
            reconnectJitter = 0.0,
            connectTimeoutMs = 1_000,
            statusMinIntervalMs = 50,
            errorReplyMinIntervalMs = 0,
        )
    }
}
