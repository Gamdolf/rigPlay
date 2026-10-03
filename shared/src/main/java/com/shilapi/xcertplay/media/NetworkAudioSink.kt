package com.shilapi.xcertplay.media

import android.util.Log
import com.shilapi.xcertplay.airplay.AudioFormat
import com.shilapi.xcertplay.airplay.AudioStreamId
import com.shilapi.xcertplay.airplay.MediaSink
import com.shilapi.xcertplay.simhub.AudioStream as PcStream
import com.shilapi.xcertplay.simhub.SimHubAudioTransport
import com.shilapi.xcertplay.simhub.SimHubEndpoints
import java.io.Closeable
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/**
 * The audio half of a [MediaSink] that plays CarPlay audio on the PC through the SimHub plugin
 * (`docs/protocol.md` §10). Video, microphone and iAP2 callbacks are ignored: [SwitchingMediaSink]
 * sends those to the tablet's [AndroidMediaSink].
 *
 * Each CarPlay audio stream gets a sender with its own thread. The engine's receive thread only puts
 * RTP packets into the sender's [DropOldestQueue] (the oldest is dropped and counted when the PC side
 * falls behind). The sender decodes them ([PcmDecoder]), converts the PCM to s16le at a header-legal
 * rate ([PcmConversion], [LinearResampler] for 11025/22050), cuts it into 5 ms datagrams
 * ([PcmPacketizer]) and hands them to the [SimHubAudioTransport]. It sends `audioStart` before the
 * first datagram, again after a format change or a reconnect (§10.1), and `audioStop` when CarPlay
 * stops the stream or nothing arrived for [idleStopMillis].
 *
 * CarPlay streams map to the protocol's three streams by audio type: telephony → `telephony`, Siri
 * and alternate audio (guidance, alerts) → `alt`, music → `media`. A stream whose protocol stream is
 * taken by another live CarPlay stream borrows a free one; with none free it is not sent.
 *
 * No Android audio is played, so no audio focus is requested.
 */
class NetworkAudioSink(
    private val transport: () -> SimHubAudioTransport? = { SimHubEndpoints.audioTransport },
    private val decoderFactory: (AudioFormat) -> PcmDecoder = PcmDecoder::forFormat,
    private val queueCapacity: Int = DEFAULT_QUEUE_PACKETS,
    private val idleStopMillis: Long = DEFAULT_IDLE_STOP_MILLIS,
    private val log: (String) -> Unit = { Log.i(TAG, it) },
) : MediaSink, Closeable {
    /** Totals since the sink was created. */
    data class Stats(
        val datagramsSent: Long,
        val queueDrops: Long,
        val sendFailures: Long,
        val noTargetDrops: Long,
        val decodeErrors: Long,
        val unassignedStreams: Long,
    )

    private val datagramsSent = AtomicLong()
    private val queueDrops = AtomicLong()
    private val sendFailures = AtomicLong()
    private val noTargetDrops = AtomicLong()
    private val decodeErrors = AtomicLong()
    private val unassignedStreams = AtomicLong()

    private val senders = ConcurrentHashMap<AudioStreamId, StreamSender>()
    private val assignmentLock = Any()
    private val holders = HashMap<PcStream, StreamSender>()
    private val lastHolders = HashMap<PcStream, StreamSender>()

    val stats: Stats
        get() = Stats(
            datagramsSent.get(), queueDrops.get(), sendFailures.get(),
            noTargetDrops.get(), decodeErrors.get(), unassignedStreams.get(),
        )

    /** Protocol stream each live CarPlay stream is sent on. */
    val assignments: Map<AudioStreamId, PcStream>
        get() = senders.mapValues { it.value.stream }

    override fun onAudioStarted(id: AudioStreamId, format: AudioFormat, firstSample: Int) {
        sender(id, format)
    }

    override fun onAudioRtp(id: AudioStreamId, format: AudioFormat, rtp: ByteArray, sample: Int) {
        sender(id, format)?.submit(rtp, sample)
    }

    override fun onAudioStopped(id: AudioStreamId) {
        synchronized(assignmentLock) {
            val sender = senders.remove(id) ?: return
            holders.remove(sender.stream, sender)
            sender.stop()
        }
    }

    /** Ends every stream: buffered audio is sent and each started stream gets `audioStop`. */
    override fun close() {
        senders.keys.toList().forEach(::onAudioStopped)
    }

    private fun sender(id: AudioStreamId, format: AudioFormat): StreamSender? {
        senders[id]?.let { if (it.format == format) return it }
        synchronized(assignmentLock) {
            val existing = senders[id]
            if (existing != null) {
                if (existing.format == format) return existing
                senders.remove(id)
                holders.remove(existing.stream, existing)
                existing.stop()
            }
            val preferred = preferredStream(id)
            val stream = (listOf(preferred) + FALLBACK_ORDER).firstOrNull { it !in holders }
            if (stream == null) {
                if (unassignedStreams.getAndIncrement() == 0L) log("PC audio: no free stream for $id")
                return null
            }
            val predecessor = lastHolders[stream]?.takeUnless { it.finished }
            val sender = StreamSender(id, format, stream, predecessor)
            senders[id] = sender
            holders[stream] = sender
            lastHolders[stream] = sender
            if (stream != preferred) log("PC audio: $id sent as ${stream.wire}, ${preferred.wire} is busy")
            return sender
        }
    }

    private data class Announcement(
        val transport: SimHubAudioTransport,
        val epoch: Long,
        val target: InetSocketAddress,
        val sampleRate: Int,
        val channels: Int,
    )

    private sealed class Item {
        class Packet(val rtp: ByteArray, val sample: Int) : Item()
        object Stop : Item()
    }

    private inner class StreamSender(
        val id: AudioStreamId,
        val format: AudioFormat,
        val stream: PcStream,
        private val predecessor: StreamSender?,
    ) {
        private val queue = DropOldestQueue<Item>(queueCapacity)
        private val workerLock = Any()
        private var worker: Thread? = null
        @Volatile private var stopping = false
        private val done = CountDownLatch(1)

        // Confined to the worker thread.
        private var predecessorAwaited = false
        private var decoder: PcmDecoder? = null
        private var resampler: LinearResampler? = null
        private var packetizer: PcmPacketizer? = null
        private var announced: Announcement? = null
        private var datagramsThisStream = 0L
        private val pcmSink = PcmSink { pcm, offset, length, chunk -> onPcm(pcm, offset, length, chunk) }

        val finished: Boolean get() = done.count == 0L

        fun submit(rtp: ByteArray, sample: Int) {
            if (stopping) return
            if (queue.offer(Item.Packet(rtp, sample)) != null) queueDrops.incrementAndGet()
            ensureWorker()
        }

        fun stop() {
            stopping = true
            if (queue.offer(Item.Stop) != null) queueDrops.incrementAndGet()
            ensureWorker()
        }

        fun awaitFinished(timeoutMs: Long) {
            try {
                done.await(timeoutMs, TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }

        private fun ensureWorker() = synchronized(workerLock) {
            if (worker == null) {
                worker = Thread(::run, "rigplay-pc-audio-${stream.wire}").apply {
                    isDaemon = true
                    start()
                }
            }
        }

        private fun run() {
            var ended = false
            try {
                if (!predecessorAwaited) {
                    // The previous holder's audioStop must reach the plugin before this stream's audioStart.
                    predecessor?.awaitFinished(PREDECESSOR_WAIT_MILLIS)
                    predecessorAwaited = true
                }
                while (true) {
                    when (val item = queue.poll(idleStopMillis)) {
                        null -> {
                            endStream("idle")
                            break
                        }
                        is Item.Stop -> {
                            endStream("stopped")
                            ended = true
                            break
                        }
                        is Item.Packet -> handle(item)
                    }
                }
            } catch (error: Throwable) {
                log("PC audio: sender ${stream.wire} failed ${error.javaClass.simpleName}: ${error.message}")
                runCatching { endStream("error") }
            } finally {
                if (ended) done.countDown()
                synchronized(workerLock) {
                    worker = null
                    if (!ended && !queue.isEmpty()) ensureWorker()
                }
            }
        }

        private fun handle(packet: Item.Packet) {
            val active = decoder ?: try {
                decoderFactory(format).also { decoder = it }
            } catch (error: Exception) {
                if (decodeErrors.getAndIncrement() == 0L) log("PC audio: no decoder for ${format.codec}: $error")
                return
            }
            try {
                active.decode(packet.rtp, packet.sample, pcmSink)
            } catch (error: Exception) {
                decodeErrors.incrementAndGet()
                runCatching { active.close() }
                decoder = null
            }
        }

        private fun onPcm(pcm: ByteArray, offset: Int, length: Int, chunk: PcmChunkFormat) {
            if (chunk.channels < 1 || chunk.sampleRate <= 0) return
            var data = PcmConversion.toS16le(pcm, offset, length, chunk.encoding)
            val channels = minOf(chunk.channels, 2)
            data = PcmConversion.toAtMostStereo(data, chunk.channels)
            val rate = PcmConversion.wireRate(chunk.sampleRate)
            if (rate != chunk.sampleRate) {
                val current = resampler?.takeIf {
                    it.inputRate == chunk.sampleRate && it.outputRate == rate && it.channels == channels
                } ?: LinearResampler(chunk.sampleRate, rate, channels).also { resampler = it }
                data = current.process(data)
            } else {
                resampler = null
            }
            if (data.isNotEmpty()) send(data, rate, channels)
        }

        private fun send(pcm: ByteArray, sampleRate: Int, channels: Int) {
            val link = transport()
            val target = link?.audioTarget
            if (link == null || target == null) {
                // The plugin forgets the stream with the link (§10.1); announce it again when it is back.
                announced = null
                packetizer = null
                noTargetDrops.incrementAndGet()
                return
            }
            val wanted = Announcement(link, link.audioEpoch, target, sampleRate, channels)
            var current = packetizer
            if (announced != wanted || current == null) {
                val previous = announced
                if (previous != null && current != null && previous.copy(sampleRate = sampleRate, channels = channels) == wanted) {
                    // Format change on the same session: the old format's tail goes out under its own header.
                    current.flush { bytes, length -> deliver(link, bytes, length) }
                }
                if (!link.audioStart(stream, sampleRate, channels)) {
                    announced = null
                    packetizer = null
                    noTargetDrops.incrementAndGet()
                    return
                }
                log("PC audio: audioStart ${stream.wire} ${sampleRate}Hz x$channels for $id codec=${format.codec}")
                announced = wanted
                current = PcmPacketizer(stream, sampleRate, channels)
                packetizer = current
            }
            current.push(pcm) { bytes, length -> deliver(link, bytes, length) }
        }

        private fun deliver(link: SimHubAudioTransport, bytes: ByteArray, length: Int) {
            if (link.sendDatagram(bytes, length)) {
                datagramsSent.incrementAndGet()
                datagramsThisStream++
            } else {
                sendFailures.incrementAndGet()
            }
        }

        private fun endStream(reason: String) {
            val started = announced
            val link = transport()
            if (started != null && link != null && link === started.transport && link.audioEpoch == started.epoch) {
                if (link.audioTarget != null) packetizer?.flush { bytes, length -> deliver(link, bytes, length) }
                link.audioStop(stream)
                log("PC audio: audioStop ${stream.wire} ($reason) datagrams=$datagramsThisStream drops=${queue.dropped}")
            }
            announced = null
            packetizer = null
            resampler = null
            datagramsThisStream = 0L
            decoder?.let { runCatching { it.close() } }
            decoder = null
        }
    }

    companion object {
        const val TAG = "rigPlay-PcAudio"

        /** RTP packets buffered per stream: about 1.3 s of AAC (1024 frames per packet at 48 kHz). */
        const val DEFAULT_QUEUE_PACKETS = 64

        /** A stream that sends nothing for this long is stopped on the PC; it restarts with the next packet. */
        const val DEFAULT_IDLE_STOP_MILLIS = 3_000L

        private const val PREDECESSOR_WAIT_MILLIS = 500L

        private val FALLBACK_ORDER = listOf(PcStream.ALT, PcStream.MEDIA, PcStream.TELEPHONY)

        /** The protocol stream a CarPlay stream belongs on (§6.11). */
        fun preferredStream(id: AudioStreamId): PcStream = when (id.audioType.lowercase()) {
            "telephony" -> PcStream.TELEPHONY
            "speechrecognition" -> PcStream.ALT
            "media", "compatibility" -> PcStream.MEDIA
            "default", "alert" -> PcStream.ALT
            else -> if (id.type == AudioChannelMapper.STREAM_TYPE_MAIN_HIGH_AUDIO) PcStream.MEDIA else PcStream.ALT
        }
    }
}
