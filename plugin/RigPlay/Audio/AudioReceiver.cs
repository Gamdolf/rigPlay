// SPDX-License-Identifier: GPL-3.0-only
// AudioReceiver.cs: receives the tablet's audio datagrams (docs/protocol.md §10) on the audio UDP port, on a
// dedicated thread, and keeps one AudioStream (format + JitterBuffer) per stream type. Streams are started and
// stopped by the control channel through OnAudioStart / OnAudioStop / OnLinkLost; until the control server
// (#20) calls them, a datagram with the start flag starts its stream on its own (AutoStartOnFirstFlag, a
// documented fallback). A stream joins the output mix (IAudioSink) when its datagrams arrive and leaves it on
// audioStop, link loss or 2 s without datagrams. Every 500 ms the receiver publishes an AudioStats snapshot.
// No NAudio, SimHub or WPF types here (compiled into RigPlay.Tests); AudioOutput.cs is the NAudio side.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RigPlayPlugin.Audio
{
    /// <summary>Where the receiver's log lines go; AudioPipeline points these at SimHub's log.</summary>
    public static class AudioLog
    {
        public static Action<string> Info = message => { };
        public static Action<string> Warn = message => { };
    }

    /// <summary>The output side of the receiver: a stream joins or leaves the mix.</summary>
    public interface IAudioSink
    {
        /// <summary>Datagrams are arriving for <paramref name="stream"/>: pull its PCM from <see cref="AudioStream.Buffer"/>.</summary>
        void StreamActivated(AudioStream stream);

        /// <summary>Stop pulling from <paramref name="stream"/> (stopped, link lost, or idle for 2 s).</summary>
        void StreamDeactivated(AudioStream stream);
    }

    /// <summary>One started stream: its announced format and its play-out buffer. A format change makes a new one.</summary>
    public sealed class AudioStream
    {
        internal AudioStream(AudioStreamType type, int sampleRate, int channels, AudioFormat format, bool autoStarted, int targetMs, int maxMs)
        {
            Type = type;
            SampleRate = sampleRate;
            Channels = channels;
            Format = format;
            AutoStarted = autoStarted;
            Buffer = new JitterBuffer(sampleRate, channels, targetMs, maxMs);
        }

        public AudioStreamType Type { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        public AudioFormat Format { get; }
        public bool AutoStarted { get; }
        public JitterBuffer Buffer { get; }

        /// <summary>In the output mix.</summary>
        public bool Active { get; internal set; }

        internal long LastPacketMs;
        internal readonly RateMeter Meter = new RateMeter();

        internal bool Matches(AudioHeader header)
        {
            return header.SampleRate == SampleRate && header.Channels == Channels && header.Format == Format;
        }

        public override string ToString()
        {
            return AudioHeader.StreamName(Type) + " " + AudioStats.DescribeFormat(SampleRate, Channels, Format);
        }
    }

    public sealed class AudioReceiver : IDisposable
    {
        /// <summary>A stream with no datagram for this long leaves the mix and its buffer is flushed.</summary>
        public const int IdleTimeoutMs = 2000;

        public const int StatsIntervalMs = 500;

        private static readonly AudioStreamType[] StreamTypes = { AudioStreamType.Media, AudioStreamType.Alt, AudioStreamType.Telephony };

        private readonly object gate = new object();
        private readonly Dictionary<AudioStreamType, AudioStream> streams = new Dictionary<AudioStreamType, AudioStream>();
        private readonly IAudioSink sink;
        private readonly Func<long> clockMs;
        private readonly int targetMs;
        private readonly int maxMs;
        private readonly RateMeter datagramMeter = new RateMeter();
        private readonly HashSet<string> loggedOnce = new HashSet<string>();

        private UdpClient udp;
        private Thread thread;
        private Timer timer;
        private volatile bool running;
        private int ticking;

        private long datagrams;
        private long invalid;
        private long rejected;
        private volatile string lastError = "";
        private volatile AudioStats stats = AudioStats.Empty;

        public AudioReceiver(int port, IAudioSink sink, int targetMs = JitterBuffer.DefaultTargetMs, int maxMs = JitterBuffer.DefaultMaxMs)
            : this(port, sink, targetMs, maxMs, null)
        {
        }

        internal AudioReceiver(int port, IAudioSink sink, int targetMs, int maxMs, Func<long> clockMs)
        {
            Port = port;
            this.sink = sink;
            this.targetMs = targetMs;
            this.maxMs = maxMs;
            this.clockMs = clockMs ?? StopwatchMs;
        }

        /// <summary>The configured audio port (0 binds an ephemeral port, for tests).</summary>
        public int Port { get; private set; }

        /// <summary>The port actually bound, or 0 when not listening.</summary>
        public int BoundPort { get; private set; }

        public bool Listening
        {
            get { return running && BoundPort != 0; }
        }

        /// <summary>
        /// Fallback until the control server (#20) delivers audioStart: a datagram with the start flag for a stream
        /// that was not started starts it with the datagram's format. The control server may turn this off once it
        /// calls <see cref="OnAudioStart(AudioStreamType,int,int,AudioFormat)"/>.
        /// </summary>
        public bool AutoStartOnFirstFlag { get; set; } = true;

        /// <summary>
        /// Optional source check (§10.1: only the remote IP of a Paired session). Null accepts every source; the
        /// control server sets it once sessions exist. Called on the receive thread.
        /// </summary>
        public Func<IPAddress, bool> SourceFilter { get; set; }

        /// <summary>Supplies the output line of the stats (device and state); set by AudioPipeline.</summary>
        public Func<string> OutputStatus { get; set; }

        /// <summary>The latest snapshot, refreshed every 500 ms.</summary>
        public AudioStats Stats
        {
            get { return stats; }
        }

        /// <summary>Raised on the timer thread after each new snapshot.</summary>
        public event Action<AudioStats> StatsUpdated;

        /// <summary>Binds the port and starts the receive thread and the stats timer. Never throws.</summary>
        public void Start()
        {
            if (running) return;
            running = true;
            Bind();
            timer = new Timer(state => Tick(clockMs()), null, StatsIntervalMs, StatsIntervalMs);
        }

        /// <summary>Moves the listener to another port (the settings changed). Streams keep their state.</summary>
        public void Rebind(int port)
        {
            if (port == Port && Listening) return;
            Port = port;
            if (!running) return;
            CloseSocket();
            Bind();
        }

        // Control channel API (called by the control server, #20).

        /// <summary>
        /// audioStart for one stream (§6.11): (re)starts it with this format. A stream already started is restarted,
        /// which is how the tablet announces a format change. Returns false, and logs, for an invalid announcement.
        /// </summary>
        public bool OnAudioStart(AudioStreamType stream, int sampleRate, int channels, AudioFormat format)
        {
            if (stream != AudioStreamType.Media && stream != AudioStreamType.Alt && stream != AudioStreamType.Telephony)
            {
                AudioLog.Warn("audioStart ignored: stream " + AudioHeader.StreamName(stream) + " cannot be received");
                return false;
            }
            if (!AudioHeader.IsValidSampleRate(sampleRate) || (channels != 1 && channels != 2) || format != AudioFormat.PcmS16le)
            {
                AudioLog.Warn("audioStart ignored: " + AudioHeader.StreamName(stream) + " " + sampleRate + " Hz x" + channels + " " + AudioHeader.FormatName(format) + " is not a valid protocol 1 format");
                return false;
            }
            lock (gate)
            {
                StartLocked(stream, sampleRate, channels, format, false);
            }
            return true;
        }

        /// <summary>audioStart with the JSON member values: stream "media"/"alt"/"telephony", format "pcm_s16le".</summary>
        public bool OnAudioStart(string stream, string format, int sampleRate, int channels)
        {
            AudioStreamType type;
            AudioFormat fmt;
            if (!AudioHeader.TryParseStreamName(stream, out type) || !AudioHeader.TryParseFormatName(format, out fmt))
            {
                AudioLog.Warn("audioStart ignored: stream \"" + stream + "\" format \"" + format + "\"");
                return false;
            }
            return OnAudioStart(type, sampleRate, channels, fmt);
        }

        /// <summary>audioStop for one stream (§6.12): it leaves the mix at once and its buffer is discarded.</summary>
        public void OnAudioStop(AudioStreamType stream)
        {
            lock (gate)
            {
                StopLocked(stream, "audioStop");
            }
        }

        public void OnAudioStop(string stream)
        {
            AudioStreamType type;
            if (AudioHeader.TryParseStreamName(stream, out type)) OnAudioStop(type);
        }

        /// <summary>The session's link was lost (§9): every stream stops, as after audioStop.</summary>
        public void OnLinkLost()
        {
            lock (gate)
            {
                foreach (var type in StreamTypes) StopLocked(type, "link lost");
            }
        }

        /// <summary>Records an error for the page (the output reports device failures here).</summary>
        public void ReportError(string message)
        {
            lastError = message ?? "";
        }

        public void Dispose()
        {
            running = false;
            try { timer?.Dispose(); } catch { }
            timer = null;
            CloseSocket();
            OnLinkLost();
        }

        // Datagram path

        /// <summary>Handles one datagram. Called on the receive thread; internal for tests.</summary>
        internal void ProcessDatagram(byte[] data, int length, IPAddress from)
        {
            Interlocked.Increment(ref datagrams);
            AudioHeader header;
            var error = AudioHeader.TryParse(data, 0, length, out header);
            if (error != AudioHeaderError.Ok)
            {
                Interlocked.Increment(ref invalid);
                LogOnce("invalid:" + error, "Dropped an invalid audio datagram (" + error + ", " + length + " bytes) from " + from + "; further ones are counted silently");
                return;
            }

            var filter = SourceFilter;
            if (filter != null && !filter(from))
            {
                Interlocked.Increment(ref rejected);
                LogOnce("source:" + from, "Dropped audio from " + from + ": not a paired tablet");
                return;
            }

            var now = clockMs();
            lock (gate)
            {
                AudioStream stream;
                streams.TryGetValue(header.StreamType, out stream);
                if (stream == null || (!stream.Matches(header) && header.IsStart && stream.AutoStarted))
                {
                    if (!header.IsStart || !AutoStartOnFirstFlag)
                    {
                        Interlocked.Increment(ref rejected);
                        LogOnce("notstarted:" + header.StreamType, "Dropped " + AudioHeader.StreamName(header.StreamType) + " audio: the stream was not started (no audioStart)");
                        return;
                    }
                    stream = StartLocked(header.StreamType, header.SampleRate, header.Channels, header.Format, true);
                }
                else if (!stream.Matches(header))
                {
                    Interlocked.Increment(ref rejected);
                    LogOnce("format:" + header.StreamType, "Dropped " + AudioHeader.StreamName(header.StreamType) + " audio in " + AudioStats.DescribeFormat(header.SampleRate, header.Channels, header.Format)
                        + ": the stream was started as " + stream.ToString());
                    return;
                }

                stream.Buffer.Push(header.Seq, header.Timestamp, header.IsStart, data, AudioHeader.Size, length - AudioHeader.Size);
                stream.LastPacketMs = now;
                if (!stream.Active)
                {
                    stream.Active = true;
                    AudioLog.Info("Audio " + stream + " playing" + (stream.AutoStarted ? " (started by the first datagram)" : ""));
                    NotifyActivated(stream);
                }
            }
        }

        /// <summary>The 500 ms housekeeping: idle streams leave the mix, then a new stats snapshot. Internal for tests.</summary>
        internal void Tick(long now)
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try
            {
                var snapshot = new AudioStats
                {
                    At = DateTime.UtcNow,
                    Port = Port,
                    Listening = Listening,
                    Datagrams = Interlocked.Read(ref datagrams),
                    Invalid = Interlocked.Read(ref invalid),
                    Rejected = Interlocked.Read(ref rejected),
                    LastError = lastError,
                };
                var seconds = now / 1000.0;
                lock (gate)
                {
                    datagramMeter.Add(seconds, snapshot.Datagrams, 0);
                    snapshot.DatagramsPerSecond = datagramMeter.PacketsPerSecond;
                    foreach (var type in StreamTypes)
                    {
                        AudioStream stream;
                        if (!streams.TryGetValue(type, out stream))
                        {
                            snapshot.Streams.Add(new AudioStreamStats { Stream = type });
                            continue;
                        }
                        if (stream.Active && now - stream.LastPacketMs >= IdleTimeoutMs)
                        {
                            AudioLog.Info("Audio " + stream + ": no datagram for " + (IdleTimeoutMs / 1000) + " s, output stopped");
                            Deactivate(stream);
                        }
                        var c = stream.Buffer.Counters;
                        stream.Meter.Add(seconds, c.Received, c.Lost);
                        snapshot.Streams.Add(new AudioStreamStats
                        {
                            Stream = type,
                            Started = true,
                            Active = stream.Active,
                            AutoStarted = stream.AutoStarted,
                            SampleRate = stream.SampleRate,
                            Channels = stream.Channels,
                            Format = stream.Format,
                            PacketsPerSecond = stream.Meter.PacketsPerSecond,
                            LossPercent = stream.Meter.LossPercent,
                            BufferMs = c.BufferedFrames * 1000.0 / stream.SampleRate,
                            Received = c.Received,
                            Lost = c.Lost,
                            Late = c.Late,
                            Underruns = c.Underruns,
                        });
                    }
                }
                try
                {
                    var output = OutputStatus;
                    if (output != null) snapshot.Output = output() ?? "";
                }
                catch (Exception ex)
                {
                    snapshot.Output = "Unknown (" + ex.Message + ")";
                }
                stats = snapshot;
                try { StatsUpdated?.Invoke(snapshot); } catch (Exception ex) { AudioLog.Warn("A stats listener failed: " + ex.Message); }
            }
            catch (Exception ex)
            {
                AudioLog.Warn("Audio housekeeping failed: " + ex);
            }
            finally
            {
                Interlocked.Exchange(ref ticking, 0);
            }
        }

        /// <summary>True while <paramref name="type"/> is started; for tests and the control server.</summary>
        public bool IsStarted(AudioStreamType type)
        {
            lock (gate) return streams.ContainsKey(type);
        }

        /// <summary>The started stream of this type, or null.</summary>
        public AudioStream GetStream(AudioStreamType type)
        {
            lock (gate)
            {
                AudioStream stream;
                return streams.TryGetValue(type, out stream) ? stream : null;
            }
        }

        private AudioStream StartLocked(AudioStreamType type, int sampleRate, int channels, AudioFormat format, bool auto)
        {
            AudioStream old;
            if (streams.TryGetValue(type, out old))
            {
                Deactivate(old);
                streams.Remove(type);
            }
            var stream = new AudioStream(type, sampleRate, channels, format, auto, targetMs, maxMs);
            stream.Meter.Add(clockMs() / 1000.0, 0, 0); // so the first snapshot already has a rate
            streams[type] = stream;
            AudioLog.Info("Audio stream " + stream + (old == null ? "" : " (restarted)") + " started" + (auto ? " by its first datagram (fallback until the control channel sends audioStart)" : ""));
            return stream;
        }

        private void StopLocked(AudioStreamType type, string reason)
        {
            AudioStream stream;
            if (!streams.TryGetValue(type, out stream)) return;
            streams.Remove(type);
            Deactivate(stream);
            AudioLog.Info("Audio stream " + stream + " stopped (" + reason + ")");
        }

        private void Deactivate(AudioStream stream)
        {
            if (stream.Active)
            {
                stream.Active = false;
                try { sink?.StreamDeactivated(stream); } catch (Exception ex) { AudioLog.Warn("The audio output failed to remove a stream: " + ex.Message); }
            }
            stream.Buffer.Reset();
        }

        private void NotifyActivated(AudioStream stream)
        {
            try { sink?.StreamActivated(stream); } catch (Exception ex) { AudioLog.Warn("The audio output failed to add a stream: " + ex.Message); }
        }

        // Socket

        private void Bind()
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                try
                {
                    client.Client.ReceiveBufferSize = 1 << 20;
                    DisableConnectionReset(client.Client);
                    client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                }
                catch
                {
                    client.Close();
                    throw;
                }
                udp = client;
                BoundPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
                var t = new Thread(() => ReceiveLoop(client))
                {
                    IsBackground = true,
                    Name = "rigPlay audio receiver",
                    Priority = ThreadPriority.AboveNormal,
                };
                thread = t;
                t.Start();
                AudioLog.Info("Audio receiver listening on UDP port " + BoundPort);
                if (lastError.StartsWith("Audio port", StringComparison.Ordinal)) lastError = "";
            }
            catch (Exception ex)
            {
                BoundPort = 0;
                lastError = "Audio port " + Port + " could not be bound: " + ex.Message;
                AudioLog.Warn(lastError + ". rigPlay keeps running without audio.");
            }
        }

        private void CloseSocket()
        {
            var client = udp;
            var t = thread;
            udp = null;
            thread = null;
            BoundPort = 0;
            try { client?.Close(); } catch { }
            if (t != null && t != Thread.CurrentThread)
            {
                try { t.Join(1000); } catch { }
            }
        }

        private void ReceiveLoop(UdpClient client)
        {
            var buffer = new byte[65536];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (running && ReferenceEquals(udp, client))
            {
                int length;
                try
                {
                    length = client.Client.ReceiveFrom(buffer, ref remote);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (!running || !ReferenceEquals(udp, client)) break;
                    // ICMP port unreachable from an earlier send, or an oversized datagram: not fatal.
                    if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize) continue;
                    lastError = "Audio receive failed: " + ex.Message;
                    AudioLog.Warn(lastError);
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    ProcessDatagram(buffer, length, ((IPEndPoint)remote).Address);
                }
                catch (Exception ex)
                {
                    lastError = "Audio datagram handling failed: " + ex.Message;
                    LogOnce("process:" + ex.GetType().Name, lastError + " " + ex);
                }
            }
        }

        /// <summary>Windows reports an ICMP port-unreachable as a receive error on UDP sockets; turn that off.</summary>
        private static void DisableConnectionReset(Socket socket)
        {
            try
            {
                const int SioUdpConnReset = unchecked((int)0x9800000C);
                socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch
            {
                // Not Windows, or not supported: nothing to turn off.
            }
        }

        private void LogOnce(string key, string message)
        {
            lock (loggedOnce)
            {
                if (loggedOnce.Count > 256) return;
                if (!loggedOnce.Add(key)) return;
            }
            AudioLog.Warn(message);
        }

        private static long StopwatchMs()
        {
            return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        }
    }
}
