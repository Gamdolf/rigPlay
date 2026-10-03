// SPDX-License-Identifier: GPL-3.0-only
// AudioStats.cs: what the audio receiver reports to the rigPlay page: per stream packets/s, loss %, buffer depth
// and format, plus totals, the output device and the last error. The receiver takes a snapshot every 500 ms;
// RateMeter turns cumulative counters into rates over a short sliding window. Pure: no SimHub, WPF or NAudio
// types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RigPlayPlugin.Audio
{
    /// <summary>One stream's line on the page.</summary>
    public sealed class AudioStreamStats
    {
        public AudioStreamType Stream;
        /// <summary>audioStart received (or the start-flag fallback fired) and no audioStop / link loss since.</summary>
        public bool Started;
        /// <summary>Datagrams arriving and the stream is in the mix (false after 2 s without datagrams).</summary>
        public bool Active;
        /// <summary>Started by the first datagram's start flag rather than by audioStart (fallback).</summary>
        public bool AutoStarted;
        public int SampleRate;
        public int Channels;
        public AudioFormat Format;
        public double PacketsPerSecond;
        public double LossPercent;
        public double BufferMs;
        public long Received;
        public long Lost;
        public long Late;
        public long Underruns;

        public string FormatText
        {
            get { return AudioStats.DescribeFormat(SampleRate, Channels, Format); }
        }

        /// <summary>E.g. "200 pkt/s · loss 5.0 % · buffer 82 ms · 48 kHz stereo pcm_s16le", or "stopped".</summary>
        public string ToDisplayString()
        {
            if (!Started) return "stopped";
            var inv = CultureInfo.InvariantCulture;
            var state = Active ? "" : "idle · ";
            return state
                + PacketsPerSecond.ToString("0", inv) + " pkt/s · loss "
                + LossPercent.ToString("0.0", inv) + " % · buffer "
                + BufferMs.ToString("0", inv) + " ms · "
                + FormatText
                + (AutoStarted ? " (auto-started)" : "");
        }
    }

    /// <summary>A snapshot of the whole receiver.</summary>
    public sealed class AudioStats
    {
        public static readonly AudioStats Empty = new AudioStats();

        public DateTime At;
        public bool Listening;
        public int Port;
        /// <summary>All datagrams that reached the socket.</summary>
        public long Datagrams;
        public double DatagramsPerSecond;
        /// <summary>Datagrams with a malformed or reserved header.</summary>
        public long Invalid;
        /// <summary>Valid datagrams dropped by policy: stream not started, wrong source, format mismatch.</summary>
        public long Rejected;
        /// <summary>What the output is doing, e.g. "Playing on Speakers (Realtek)" or "No output device".</summary>
        public string Output = "Idle";
        public string LastError = "";
        public List<AudioStreamStats> Streams = new List<AudioStreamStats>();

        public AudioStreamStats Find(AudioStreamType type)
        {
            foreach (var s in Streams)
            {
                if (s.Stream == type) return s;
            }
            return null;
        }

        /// <summary>E.g. "48 kHz stereo pcm_s16le", "44.1 kHz stereo pcm_s16le", "16 kHz mono pcm_s16le".</summary>
        public static string DescribeFormat(int sampleRate, int channels, AudioFormat format)
        {
            if (sampleRate <= 0) return "no format";
            var khz = (sampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            var layout = channels == 1 ? "mono" : channels == 2 ? "stereo" : channels + " ch";
            return khz + " kHz " + layout + " " + AudioHeader.FormatName(format);
        }
    }

    /// <summary>
    /// Rates from cumulative counters, over a sliding window (default 2 s) so the page does not flicker:
    /// packets per second, and loss as lost / expected datagrams in the window.
    /// </summary>
    public sealed class RateMeter
    {
        private struct Sample
        {
            public double T;
            public long Received;
            public long Lost;
        }

        private readonly Queue<Sample> samples = new Queue<Sample>();
        private readonly double windowSeconds;

        public RateMeter(double windowSeconds = 2.0)
        {
            this.windowSeconds = windowSeconds;
        }

        public double PacketsPerSecond { get; private set; }
        public double LossPercent { get; private set; }

        /// <summary>Records the counters at time <paramref name="seconds"/> (any monotonic clock) and updates the rates.</summary>
        public void Add(double seconds, long received, long lost)
        {
            samples.Enqueue(new Sample { T = seconds, Received = received, Lost = lost });
            while (samples.Count > 2 && seconds - samples.Peek().T > windowSeconds) samples.Dequeue();

            var first = samples.Peek();
            var dt = seconds - first.T;
            if (dt <= 0)
            {
                PacketsPerSecond = 0;
                LossPercent = 0;
                return;
            }
            var dReceived = Math.Max(0, received - first.Received);
            var dLost = Math.Max(0, lost - first.Lost);
            PacketsPerSecond = dReceived / dt;
            LossPercent = Compute(dReceived, dLost);
        }

        public void Clear()
        {
            samples.Clear();
            PacketsPerSecond = 0;
            LossPercent = 0;
        }

        /// <summary>lost / (received + lost) in percent; 0 when nothing was expected.</summary>
        public static double Compute(long received, long lost)
        {
            var expected = received + lost;
            return expected <= 0 ? 0 : 100.0 * lost / expected;
        }
    }
}
