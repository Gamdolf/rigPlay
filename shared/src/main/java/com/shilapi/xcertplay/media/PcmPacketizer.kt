package com.shilapi.xcertplay.media

import com.shilapi.xcertplay.simhub.AudioHeader
import com.shilapi.xcertplay.simhub.AudioStream
import com.shilapi.xcertplay.simhub.SimHubAudioCodec

/**
 * Cuts one stream's s16le PCM into audio datagrams (`docs/protocol.md` §10.2): [framesPerDatagram]
 * frames each, `seq` from 0 and +1 per datagram, `timestamp` = frames sent before the datagram, and
 * the `start` flag on the first one. A new packetizer belongs to each `audioStart`.
 */
class PcmPacketizer(
    val stream: AudioStream,
    val sampleRate: Int,
    val channels: Int,
    val framesPerDatagram: Int = defaultFramesPerDatagram(sampleRate, channels),
) {
    init {
        require(PcmConversion.isWireRate(sampleRate)) { "sampleRate must be a multiple of 100 in 8000..48000" }
        require(channels == 1 || channels == 2) { "channels must be 1 or 2" }
        require(framesPerDatagram >= 1 && framesPerDatagram * 2 * channels <= MAX_PAYLOAD_BYTES) {
            "a datagram must hold 1..${MAX_PAYLOAD_BYTES / (2 * channels)} frames"
        }
    }

    private val frameBytes = 2 * channels
    private val payloadBytes = framesPerDatagram * frameBytes
    private val datagram = ByteArray(AudioHeader.SIZE + payloadBytes)
    private var pending = 0

    /** `seq` of the next datagram. */
    var seq: Int = 0
        private set

    /** `timestamp` of the next datagram: frames sent so far, modulo 2^32. */
    var timestamp: Long = 0L
        private set

    var datagrams: Long = 0L
        private set

    /** Bytes buffered towards the next datagram. */
    val pendingBytes: Int get() = pending

    /**
     * Appends PCM and calls [emit] once per complete datagram with a buffer and its length. The buffer
     * is reused: it is valid only during the call.
     */
    fun push(pcm: ByteArray, offset: Int = 0, length: Int = pcm.size - offset, emit: (ByteArray, Int) -> Unit) {
        var cursor = offset
        var left = length
        while (left > 0) {
            val take = minOf(left, payloadBytes - pending)
            System.arraycopy(pcm, cursor, datagram, AudioHeader.SIZE + pending, take)
            pending += take
            cursor += take
            left -= take
            if (pending == payloadBytes) emitPending(emit)
        }
    }

    /** Sends the buffered whole frames as a short datagram (end of stream); a partial frame is dropped. */
    fun flush(emit: (ByteArray, Int) -> Unit) {
        pending -= pending % frameBytes
        if (pending > 0) emitPending(emit)
        pending = 0
    }

    private fun emitPending(emit: (ByteArray, Int) -> Unit) {
        val frames = pending / frameBytes
        SimHubAudioCodec.encodeHeader(
            AudioHeader(
                seq = seq,
                stream = stream,
                start = datagrams == 0L,
                timestamp = timestamp,
                sampleRateHz = sampleRate,
                channels = channels,
            ),
            datagram,
        )
        val length = AudioHeader.SIZE + frames * frameBytes
        pending = 0
        seq = (seq + 1) and 0xFFFF
        timestamp = (timestamp + frames) and 0xFFFF_FFFFL
        datagrams++
        emit(datagram, length)
    }

    companion object {
        /** Keeps a datagram within one Ethernet frame (1472 bytes, §10.2). */
        const val MAX_PAYLOAD_BYTES = 1460

        /** Recommended datagram duration (§10.2): 5 ms, 240 frames = 960 bytes at 48 kHz stereo. */
        const val DATAGRAM_MILLIS = 5

        fun defaultFramesPerDatagram(sampleRate: Int, channels: Int): Int =
            (sampleRate * DATAGRAM_MILLIS / 1000).coerceIn(1, MAX_PAYLOAD_BYTES / (2 * channels))
    }
}
