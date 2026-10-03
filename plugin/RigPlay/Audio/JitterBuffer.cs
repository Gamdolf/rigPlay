// SPDX-License-Identifier: GPL-3.0-only
// JitterBuffer.cs: the per-stream play-out buffer of the audio receiver (docs/protocol.md §10.3). Datagrams are
// pushed from the network thread in any order; the audio output pulls PCM at its own clock. Each datagram is
// placed by its timestamp (sample clock), so reordering within the buffer depth is free, a gap plays as
// silence, and a datagram that arrives after its play-out time is dropped. After a reset (start flag,
// audioStart, underrun) the buffer holds back play-out until it has the target depth; above the maximum depth it
// skips ahead to the target. Pure: no SimHub, WPF or NAudio types (compiled into RigPlay.Tests). Thread-safe.
using System;
using System.Collections.Generic;

namespace RigPlayPlugin.Audio
{
    public sealed class JitterBuffer
    {
        public const int DefaultTargetMs = 80;
        public const int DefaultMaxMs = 200;

        private sealed class Packet
        {
            public long Start;
            public int Frames;
            public byte[] Data;

            public long End
            {
                get { return Start + Frames; }
            }
        }

        private readonly object gate = new object();
        private readonly SortedList<long, Packet> packets = new SortedList<long, Packet>();

        // Sequence and timestamp tracking, unwrapped to 64 bits within one epoch (between resets).
        private bool haveSeq;
        private long firstSeq;
        private long highestSeq;
        private bool haveTimestamp;
        private long lastTimestamp;
        private bool epochFromStart;
        private byte[] startPayload;

        // Sequence numbers seen in this epoch, by seq modulo the window, to tell a duplicate from a reordered datagram.
        private const int SeenWindow = 1024;
        private readonly long[] seen = new long[SeenWindow];

        private bool playing;
        private long readPos;
        // After an underrun: datagrams ending at or before this position are late even while refilling.
        private long floor = long.MinValue;

        // Counters, cumulative since construction (resets do not clear them, so rates stay continuous).
        private long received;
        private long lost;
        private long late;
        private long duplicates;
        private long silenceFrames;
        private long underruns;
        private long overflowFrames;
        private long resets;
        private long expected;

        public JitterBuffer(int sampleRate, int channels, int targetMs = DefaultTargetMs, int maxMs = DefaultMaxMs)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (targetMs < 0 || maxMs < targetMs) throw new ArgumentOutOfRangeException(nameof(maxMs));
            SampleRate = sampleRate;
            Channels = channels;
            BlockAlign = 2 * channels;
            TargetFrames = (int)((long)sampleRate * targetMs / 1000);
            MaxFrames = (int)((long)sampleRate * maxMs / 1000);
            for (var i = 0; i < seen.Length; i++) seen[i] = long.MinValue;
        }

        public int SampleRate { get; }
        public int Channels { get; }

        /// <summary>Bytes per frame (s16, interleaved).</summary>
        public int BlockAlign { get; }

        /// <summary>Depth the buffer fills to before play-out starts.</summary>
        public int TargetFrames { get; }

        /// <summary>Depth above which the buffer skips ahead to <see cref="TargetFrames"/>.</summary>
        public int MaxFrames { get; }

        /// <summary>True while play-out runs; false while (re)filling to the target depth.</summary>
        public bool IsPlaying
        {
            get { lock (gate) return playing; }
        }

        /// <summary>Frames between the play-out position and the end of the newest datagram.</summary>
        public int BufferedFrames
        {
            get { lock (gate) return BufferedFramesLocked(); }
        }

        public double BufferedMs
        {
            get { return BufferedFrames * 1000.0 / SampleRate; }
        }

        /// <summary>
        /// Adds one datagram. <paramref name="payload"/> holds whole s16 frames (it is copied). A datagram with the
        /// start flag begins a new epoch: the buffer is reset first, unless it is a repeat of the start datagram
        /// that began the current one. Returns false when the datagram was dropped (late or duplicate).
        /// </summary>
        public bool Push(ushort seq, uint timestamp, bool start, byte[] payload, int offset, int count)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var frames = count / BlockAlign;
            if (frames <= 0) return false;

            lock (gate)
            {
                if (start)
                {
                    if (IsRepeatedStartLocked(seq, timestamp, payload, offset, frames * BlockAlign))
                    {
                        // UDP duplicated the start datagram: not a new epoch.
                        duplicates++;
                        return false;
                    }
                    ResetLocked();
                    epochFromStart = true;
                    startPayload = new byte[frames * BlockAlign];
                    Buffer.BlockCopy(payload, offset, startPayload, 0, startPayload.Length);
                }

                var extSeq = UnwrapSeq(seq);
                var extTs = UnwrapTimestamp(timestamp);
                if (haveSeq)
                {
                    if (extSeq <= highestSeq - SeenWindow || seen[Slot(extSeq)] == extSeq)
                    {
                        duplicates++;
                        return false;
                    }
                }
                seen[Slot(extSeq)] = extSeq;
                received++;
                if (!haveSeq)
                {
                    haveSeq = true;
                    firstSeq = extSeq;
                    highestSeq = extSeq;
                    expected++;
                }
                else if (extSeq > highestSeq)
                {
                    var gap = extSeq - highestSeq - 1;
                    lost += gap;
                    expected += gap + 1;
                    highestSeq = extSeq;
                }
                else
                {
                    // Out of order: it was counted as lost when the gap opened.
                    if (lost > 0) lost--;
                }

                if (!haveTimestamp || extTs > lastTimestamp)
                {
                    haveTimestamp = true;
                    lastTimestamp = extTs;
                }

                var end = extTs + frames;
                if (end <= floor || (playing && end <= readPos))
                {
                    late++;
                    return false;
                }
                if (packets.ContainsKey(extTs))
                {
                    duplicates++;
                    return false;
                }

                var data = new byte[frames * BlockAlign];
                Buffer.BlockCopy(payload, offset, data, 0, data.Length);
                packets.Add(extTs, new Packet { Start = extTs, Frames = frames, Data = data });

                if (!playing)
                {
                    // Filling: play-out starts at the oldest datagram held.
                    readPos = Math.Max(packets.Keys[0], floor);
                    if (BufferedFramesLocked() >= TargetFrames) playing = true;
                }
                else if (BufferedFramesLocked() > MaxFrames)
                {
                    // Too deep (sender clock faster than ours, or a burst): skip ahead to the target depth.
                    var newPos = MaxEndLocked() - TargetFrames;
                    overflowFrames += newPos - readPos;
                    readPos = newPos;
                    DropBeforeLocked(readPos);
                }
                return true;
            }
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with <paramref name="count"/> bytes of play-out (rounded down to whole frames;
        /// any remainder is zeroed). Always returns <paramref name="count"/>: silence while filling, for gaps and on
        /// underrun, so an output mixer never sees the stream end.
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / BlockAlign;
            var written = 0;
            lock (gate)
            {
                if (!playing)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                while (written < frames)
                {
                    DropBeforeLocked(readPos);
                    var need = frames - written;
                    var dst = offset + written * BlockAlign;
                    if (packets.Count == 0)
                    {
                        // Underrun: nothing left. Play silence and refill to the target before resuming.
                        Array.Clear(buffer, dst, need * BlockAlign);
                        silenceFrames += need;
                        underruns++;
                        playing = false;
                        floor = readPos;
                        written = frames;
                        break;
                    }
                    var packet = packets.Values[0];
                    if (packet.Start > readPos)
                    {
                        // A gap (lost or skipped audio): silence up to the next datagram.
                        var gap = (int)Math.Min(need, packet.Start - readPos);
                        Array.Clear(buffer, dst, gap * BlockAlign);
                        silenceFrames += gap;
                        readPos += gap;
                        written += gap;
                        continue;
                    }
                    var from = (int)(readPos - packet.Start);
                    var take = Math.Min(need, packet.Frames - from);
                    Buffer.BlockCopy(packet.Data, from * BlockAlign, buffer, dst, take * BlockAlign);
                    readPos += take;
                    written += take;
                }
            }
            var tail = count - frames * BlockAlign;
            if (tail > 0) Array.Clear(buffer, offset + frames * BlockAlign, tail);
            return count;
        }

        /// <summary>Discards everything buffered and waits for the target depth again. Counters are kept.</summary>
        public void Reset()
        {
            lock (gate) ResetLocked();
        }

        public JitterBufferCounters Counters
        {
            get
            {
                lock (gate)
                {
                    return new JitterBufferCounters
                    {
                        Received = received,
                        Expected = expected,
                        Lost = lost,
                        Late = late,
                        Duplicates = duplicates,
                        SilenceFrames = silenceFrames,
                        Underruns = underruns,
                        OverflowFrames = overflowFrames,
                        Resets = resets,
                        BufferedFrames = BufferedFramesLocked(),
                        Playing = playing,
                    };
                }
            }
        }

        private void ResetLocked()
        {
            packets.Clear();
            playing = false;
            readPos = 0;
            floor = long.MinValue;
            for (var i = 0; i < seen.Length; i++) seen[i] = long.MinValue;
            haveSeq = false;
            haveTimestamp = false;
            epochFromStart = false;
            startPayload = null;
            resets++;
        }

        private int BufferedFramesLocked()
        {
            if (packets.Count == 0) return 0;
            var buffered = MaxEndLocked() - readPos;
            return buffered <= 0 ? 0 : (int)Math.Min(int.MaxValue, buffered);
        }

        private long MaxEndLocked()
        {
            long max = long.MinValue;
            var values = packets.Values;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].End > max) max = values[i].End;
            }
            return max;
        }

        /// <summary>
        /// A start datagram identical to the one that began this epoch, arriving within the first few datagrams.
        /// A real restart (a new audioStart) also has seq 0 and timestamp 0, so the payload must match too.
        /// </summary>
        private bool IsRepeatedStartLocked(ushort seq, uint timestamp, byte[] payload, int offset, int count)
        {
            const int RepeatWindow = 8;
            if (!epochFromStart || !haveSeq || startPayload == null) return false;
            if (seq != (ushort)firstSeq || timestamp != 0 || highestSeq - firstSeq >= RepeatWindow) return false;
            if (count != startPayload.Length) return false;
            for (var i = 0; i < count; i++)
            {
                if (payload[offset + i] != startPayload[i]) return false;
            }
            return true;
        }

        private static int Slot(long extSeq)
        {
            return (int)(((extSeq % SeenWindow) + SeenWindow) % SeenWindow);
        }

        private void DropBeforeLocked(long position)
        {
            while (packets.Count > 0 && packets.Values[0].End <= position) packets.RemoveAt(0);
        }

        /// <summary>RFC 1982 unwrap of a 16-bit sequence number around the highest one seen.</summary>
        private long UnwrapSeq(ushort seq)
        {
            if (!haveSeq) return seq;
            var delta = (short)(seq - (ushort)highestSeq);
            return highestSeq + delta;
        }

        /// <summary>Unwrap of a 32-bit sample clock around the newest timestamp seen.</summary>
        private long UnwrapTimestamp(uint timestamp)
        {
            if (!haveTimestamp) return timestamp;
            var delta = (int)(timestamp - (uint)lastTimestamp);
            return lastTimestamp + delta;
        }
    }

    /// <summary>A snapshot of a jitter buffer's cumulative counters.</summary>
    public struct JitterBufferCounters
    {
        /// <summary>Distinct datagrams pushed, including late ones; duplicates are not counted.</summary>
        public long Received;
        /// <summary>Datagrams the sequence numbers say were sent (received + lost, within epochs).</summary>
        public long Expected;
        /// <summary>Sequence numbers never received (a reordered datagram that turns up is taken back off).</summary>
        public long Lost;
        /// <summary>Datagrams dropped because their play-out time had passed.</summary>
        public long Late;
        public long Duplicates;
        /// <summary>Frames played as silence: gaps and underruns (not the initial fill).</summary>
        public long SilenceFrames;
        public long Underruns;
        /// <summary>Frames skipped because the buffer exceeded its maximum depth.</summary>
        public long OverflowFrames;
        public long Resets;
        public int BufferedFrames;
        public bool Playing;
    }
}
