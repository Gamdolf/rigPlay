// SPDX-License-Identifier: GPL-3.0-only
// AudioPipeline.cs: wires the audio receiver (UDP + jitter buffers) to the NAudio output, points their logs at
// SimHub's log and starts them. The plugin creates one in Init and disposes it in End; the control server (#20)
// forwards audioStart / audioStop / link loss through it. Constructing it never throws: without NAudio or a
// sound card the receiver still runs and the page shows why nothing plays.
using System;
using System.Runtime.CompilerServices;

namespace RigPlayPlugin.Audio
{
    public sealed class AudioPipeline : IDisposable
    {
        private readonly Func<RigPlaySettings> settings;

        /// <param name="settings">The live settings (port, device, volume, mute), read on every use so changes apply at once.</param>
        public AudioPipeline(Func<RigPlaySettings> settings)
        {
            this.settings = settings;
            AudioLog.Info = Log.Info;
            AudioLog.Warn = Log.Warn;

            var port = settings()?.AudioPort ?? ProtocolDefaults.AudioPort;
            Receiver = new AudioReceiver(port, new SinkProxy(this));
            try
            {
                output = CreateOutput(settings, Receiver);
            }
            catch (Exception ex)
            {
                Log.Error("The audio output could not be created (NAudio missing?); audio is received but not played", ex);
                Receiver.ReportError("The audio output is unavailable: " + ex.Message);
                Receiver.OutputStatus = () => "Unavailable";
            }
            Receiver.Start();
        }

        public AudioReceiver Receiver { get; }

        // Typed as object so that this class loads even when NAudio cannot (AudioOutput references it).
        private object output;

        /// <summary>Null when NAudio could not be loaded.</summary>
        public AudioOutput Output
        {
            get { return output as AudioOutput; }
        }

        /// <summary>The latest stats snapshot, refreshed every 500 ms.</summary>
        public AudioStats Stats
        {
            get { return Receiver.Stats; }
        }

        // Control channel API, for the control server (#20). See AudioReceiver for the semantics.

        /// <summary>audioStart (§6.11) with the JSON values, e.g. ("media", "pcm_s16le", 48000, 2). False when invalid.</summary>
        public bool OnAudioStart(string stream, string format, int sampleRate, int channels)
        {
            return Receiver.OnAudioStart(stream, format, sampleRate, channels);
        }

        public bool OnAudioStart(AudioStreamType stream, int sampleRate, int channels, AudioFormat format)
        {
            return Receiver.OnAudioStart(stream, sampleRate, channels, format);
        }

        /// <summary>audioStop (§6.12), e.g. ("media").</summary>
        public void OnAudioStop(string stream)
        {
            Receiver.OnAudioStop(stream);
        }

        public void OnAudioStop(AudioStreamType stream)
        {
            Receiver.OnAudioStop(stream);
        }

        /// <summary>Link loss (§9): stops every stream.</summary>
        public void OnLinkLost()
        {
            Receiver.OnLinkLost();
        }

        /// <summary>The audio port setting changed.</summary>
        public void ApplyPort()
        {
            var s = settings();
            if (s != null) Receiver.Rebind(s.AudioPort);
        }

        public void Dispose()
        {
            try { Receiver.Dispose(); } catch (Exception ex) { Log.Warn("Stopping the audio receiver failed: " + ex.Message); }
            try { (output as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Warn("Stopping the audio output failed: " + ex.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object CreateOutput(Func<RigPlaySettings> settings, AudioReceiver receiver)
        {
            var created = new AudioOutput(settings);
            created.ErrorReported += receiver.ReportError;
            receiver.OutputStatus = created.Status;
            return created;
        }

        /// <summary>Forwards the receiver's sink calls to the output once it exists (and drops them when it does not).</summary>
        private sealed class SinkProxy : IAudioSink
        {
            private readonly AudioPipeline owner;

            public SinkProxy(AudioPipeline owner)
            {
                this.owner = owner;
            }

            public void StreamActivated(AudioStream stream)
            {
                (owner.output as IAudioSink)?.StreamActivated(stream);
            }

            public void StreamDeactivated(AudioStream stream)
            {
                (owner.output as IAudioSink)?.StreamDeactivated(stream);
            }
        }
    }
}
