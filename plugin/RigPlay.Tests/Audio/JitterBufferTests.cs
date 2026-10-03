// SPDX-License-Identifier: GPL-3.0-only
// JitterBufferTests.cs: the play-out buffer: fill to the target before playing, in-order and reordered
// play-out, silence for gaps with loss counted, late and duplicate drops, reset on the start flag, skip-ahead
// above the maximum depth, underrun refill, and sequence / timestamp wrap-around.
using System;
using System.Linq;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class JitterBufferTests
    {
        // 1 kHz mono keeps the numbers small: 10 frames per datagram = 10 ms; target 30 ms, max 60 ms.
        private const int Rate = 1000;
        private const int Frames = 10;

        private static JitterBuffer NewBuffer(int targetMs = 30, int maxMs = 60, int channels = 1)
        {
            return new JitterBuffer(Rate, channels, targetMs, maxMs);
        }

        /// <summary>A datagram whose samples are all <paramref name="value"/>.</summary>
        private static byte[] Payload(short value, int frames = Frames, int channels = 1)
        {
            var bytes = new byte[frames * channels * 2];
            for (var i = 0; i < frames * channels; i++)
            {
                bytes[2 * i] = (byte)value;
                bytes[2 * i + 1] = (byte)(value >> 8);
            }
            return bytes;
        }

        private static bool Push(JitterBuffer b, int seq, long timestamp, short value, bool start = false)
        {
            var p = Payload(value, Frames, b.Channels);
            return b.Push((ushort)seq, (uint)timestamp, start, p, 0, p.Length);
        }

        private static short[] Read(JitterBuffer b, int frames)
        {
            var bytes = new byte[frames * b.BlockAlign];
            Assert.Equal(bytes.Length, b.Read(bytes, 0, bytes.Length));
            return AudioHeader.DecodeSamples(bytes, 0, bytes.Length);
        }

        [Fact]
        public void PlaysSilenceUntilTheTargetDepthIsReached()
        {
            var b = NewBuffer();
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            Assert.False(b.IsPlaying);
            Assert.Equal(20, b.BufferedFrames);
            Assert.All(Read(b, 10), s => Assert.Equal(0, s));
            Assert.Equal(20, b.BufferedFrames); // filling does not consume

            Push(b, 2, 20, 3);
            Assert.True(b.IsPlaying);
            Assert.Equal(30, b.BufferedFrames);
            Assert.Equal(Enumerable.Repeat((short)1, 10).Concat(Enumerable.Repeat((short)2, 10)), Read(b, 20));
            Assert.Equal(10, b.BufferedFrames);
            Assert.Equal(0, b.Counters.SilenceFrames);
        }

        [Fact]
        public void ReadsAcrossDatagramBoundaries()
        {
            var b = NewBuffer(targetMs: 30);
            for (var i = 0; i < 4; i++) Push(b, i, i * Frames, (short)(i + 1), start: i == 0);
            var samples = Read(b, 25);
            Assert.Equal(new short[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3 }, samples);
            Assert.Equal(new short[] { 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 }, Read(b, 15));
        }

        [Fact]
        public void FillsASequenceGapWithSilenceAndCountsTheLoss()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            // seq 2 (timestamp 20) is lost
            Push(b, 3, 30, 4);
            Push(b, 4, 40, 5);
            Assert.True(b.IsPlaying);

            var samples = Read(b, 50);
            Assert.Equal(Enumerable.Repeat((short)1, 10)
                .Concat(Enumerable.Repeat((short)2, 10))
                .Concat(Enumerable.Repeat((short)0, 10))
                .Concat(Enumerable.Repeat((short)4, 10))
                .Concat(Enumerable.Repeat((short)5, 10)), samples);

            var c = b.Counters;
            Assert.Equal(10, c.SilenceFrames);
            Assert.Equal(1, c.Lost);
            Assert.Equal(4, c.Received);
            Assert.Equal(5, c.Expected);
            Assert.Equal(0, c.Underruns);
        }

        [Fact]
        public void PlaysSilenceForATimestampJumpWithoutLoss()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 25, 2); // the sender skipped 15 frames of audio
            Read(b, 0);
            var samples = Read(b, 35);
            Assert.Equal(Enumerable.Repeat((short)1, 10).Concat(Enumerable.Repeat((short)0, 15)).Concat(Enumerable.Repeat((short)2, 10)), samples);
            Assert.Equal(15, b.Counters.SilenceFrames);
            Assert.Equal(0, b.Counters.Lost);
        }

        [Fact]
        public void ReordersWithinTheBufferAndTakesTheLossBack()
        {
            var b = NewBuffer(targetMs: 40);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            Assert.Equal(1, b.Counters.Lost);
            Push(b, 1, 10, 2);
            Assert.Equal(0, b.Counters.Lost);
            Push(b, 3, 30, 4);
            Assert.Equal(Enumerable.Range(1, 4).SelectMany(v => Enumerable.Repeat((short)v, 10)), Read(b, 40));
            Assert.Equal(0, b.Counters.SilenceFrames);
        }

        [Fact]
        public void DropsADatagramThatArrivesAfterItsPlayOutTime()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            Push(b, 3, 30, 4);
            Read(b, 20); // plays 1, then silence where seq 1 belongs
            Assert.False(Push(b, 1, 10, 2));
            var c = b.Counters;
            Assert.Equal(1, c.Late);
            Assert.Equal(10, c.SilenceFrames);
            Assert.Equal(Enumerable.Repeat((short)3, 10), Read(b, 10));
        }

        [Fact]
        public void DropsDuplicates()
        {
            var b = NewBuffer(targetMs: 30);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            Assert.False(Push(b, 1, 10, 2));
            Assert.False(Push(b, 0, 0, 1, start: true)); // a repeated start datagram is not a new epoch
            Push(b, 2, 20, 3);
            var c = b.Counters;
            Assert.Equal(2, c.Duplicates);
            Assert.Equal(3, c.Received);
            Assert.Equal(1, c.Resets);
            Assert.Equal(Enumerable.Range(1, 3).SelectMany(v => Enumerable.Repeat((short)v, 10)), Read(b, 30));
        }

        [Fact]
        public void TheStartFlagResetsTheBuffer()
        {
            var b = NewBuffer(targetMs: 30);
            for (var i = 0; i < 5; i++) Push(b, i, i * Frames, 7, start: i == 0);
            Read(b, 10);
            Assert.True(b.IsPlaying);
            Assert.Equal(40, b.BufferedFrames);

            // The tablet restarted the stream: seq and timestamp start again at 0.
            Push(b, 0, 0, 9, start: true);
            Assert.False(b.IsPlaying);
            Assert.Equal(10, b.BufferedFrames);
            Assert.All(Read(b, 10), s => Assert.Equal(0, s));
            Push(b, 1, 10, 9);
            Push(b, 2, 20, 9);
            Assert.All(Read(b, 30), s => Assert.Equal(9, s));
            Assert.Equal(2, b.Counters.Resets);
            Assert.Equal(0, b.Counters.Lost);
        }

        [Fact]
        public void SkipsAheadToTheTargetAboveTheMaximumDepth()
        {
            var b = NewBuffer(targetMs: 30, maxMs: 60);
            for (var i = 0; i < 3; i++) Push(b, i, i * Frames, (short)i, start: i == 0);
            Assert.True(b.IsPlaying);
            for (var i = 3; i < 6; i++) Push(b, i, i * Frames, (short)i);
            Assert.Equal(60, b.BufferedFrames);
            Push(b, 6, 60, 6); // 70 frames > 60: skip to 30 frames before the end
            Assert.Equal(30, b.BufferedFrames);
            Assert.Equal(40, b.Counters.OverflowFrames);
            Assert.Equal(Enumerable.Range(4, 3).SelectMany(v => Enumerable.Repeat((short)v, 10)), Read(b, 30));
        }

        [Fact]
        public void AnUnderrunPlaysSilenceAndRefillsBeforeResuming()
        {
            var b = NewBuffer(targetMs: 20);
            Push(b, 0, 0, 1, start: true);
            Push(b, 1, 10, 2);
            Assert.Equal(Enumerable.Repeat((short)1, 10).Concat(Enumerable.Repeat((short)2, 10)).Concat(Enumerable.Repeat((short)0, 5)), Read(b, 25));
            var c = b.Counters;
            Assert.Equal(1, c.Underruns);
            Assert.Equal(5, c.SilenceFrames);
            Assert.False(c.Playing);

            // The next datagram arrives just after the underrun: nothing of it was due yet, so none of it is lost.
            Push(b, 2, 20, 3);
            Assert.False(b.IsPlaying);
            Push(b, 3, 30, 4);
            Assert.True(b.IsPlaying);
            Assert.Equal(Enumerable.Repeat((short)3, 10).Concat(Enumerable.Repeat((short)4, 10)), Read(b, 20));
            Assert.Equal(0, b.Counters.Late);
        }

        [Fact]
        public void AfterAnUnderrunOlderDatagramsAreLate()
        {
            var b = NewBuffer(targetMs: 10);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            Read(b, 40); // 1, gap, 3, underrun
            Assert.False(Push(b, 1, 10, 2));
            Assert.Equal(1, b.Counters.Late);
        }

        [Fact]
        public void HandlesSequenceAndTimestampWrapAround()
        {
            var b = NewBuffer(targetMs: 30);
            const long ts = 4294967290; // 6 frames before the 32-bit wrap
            Push(b, 65534, ts, 1);
            Push(b, 65535, ts + 10, 2);
            Push(b, 0, (ts + 20) & 0xffffffff, 3);
            Push(b, 2, (ts + 40) & 0xffffffff, 5); // seq 1 lost across the wrap
            Assert.True(b.IsPlaying);
            var samples = Read(b, 50);
            Assert.Equal(Enumerable.Repeat((short)1, 10)
                .Concat(Enumerable.Repeat((short)2, 10))
                .Concat(Enumerable.Repeat((short)3, 10))
                .Concat(Enumerable.Repeat((short)0, 10))
                .Concat(Enumerable.Repeat((short)5, 10)), samples);
            Assert.Equal(1, b.Counters.Lost);
            Assert.Equal(10, b.Counters.SilenceFrames);
        }

        [Fact]
        public void StereoFramesStayInterleaved()
        {
            var b = new JitterBuffer(Rate, 2, 10, 60);
            var p = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }; // L1 R2 L3 R4
            Assert.True(b.Push(0, 0, true, p, 0, p.Length));
            Assert.Equal(2, b.BufferedFrames);
            Push(b, 1, 2, 9);
            Assert.True(b.IsPlaying);
            var bytes = new byte[8];
            b.Read(bytes, 0, 8);
            Assert.Equal(new short[] { 1, 2, 3, 4 }, AudioHeader.DecodeSamples(bytes, 0, 8));
        }

        [Fact]
        public void ReadAlwaysFillsTheRequestAndZeroesAPartialFrame()
        {
            var b = new JitterBuffer(Rate, 2, 0, 60);
            var bytes = Enumerable.Repeat((byte)0xAA, 7).ToArray();
            Assert.Equal(7, b.Read(bytes, 0, 7));
            Assert.All(bytes, x => Assert.Equal(0, x));
        }

        [Fact]
        public void TheDefaultsAreEightyAndTwoHundredMilliseconds()
        {
            var b = new JitterBuffer(48000, 2);
            Assert.Equal(3840, b.TargetFrames);
            Assert.Equal(9600, b.MaxFrames);
            Assert.Equal(4, b.BlockAlign);
        }

        [Fact]
        public void ResetKeepsTheCounters()
        {
            var b = NewBuffer(targetMs: 10);
            Push(b, 0, 0, 1, start: true);
            Push(b, 2, 20, 3);
            b.Reset();
            Assert.Equal(0, b.BufferedFrames);
            Assert.False(b.IsPlaying);
            Assert.Equal(2, b.Counters.Received);
            Assert.Equal(1, b.Counters.Lost);
        }
    }
}
