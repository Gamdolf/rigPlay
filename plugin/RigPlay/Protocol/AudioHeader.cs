// SPDX-License-Identifier: GPL-3.0-only
// AudioHeader.cs: the 12-byte header of an audio datagram (docs/protocol.md §10.2) and the codec that writes and
// checks it. Header fields are big-endian; the PCM payload is little-endian. Tested byte for byte against
// protocol/fixtures/audio-header.json.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;

namespace RigPlayPlugin.Protocol
{
    /// <summary>Header fields of one audio datagram, as they are on the wire.</summary>
    public struct AudioHeader : IEquatable<AudioHeader>
    {
        public const int Size = 12;

        public const byte StreamMedia = 1;
        public const byte StreamAlt = 2;
        public const byte StreamTelephony = 3;
        /// <summary>The PC microphone: plugin to tablet only (spec §10.4).</summary>
        public const byte StreamMic = 4;

        public const byte FlagStart = 0x01;

        public const byte FormatPcmS16Le = 1;
        public const byte FormatOpusReserved = 2;

        public const int MinSampleRateField = 80;
        public const int MaxSampleRateField = 480;

        /// <summary>Receivers accept payloads up to this size (spec §10.2).</summary>
        public const int MaxPayloadBytes = 8192;

        public ushort Seq;
        public byte StreamType;
        public byte Flags;
        public uint Timestamp;

        /// <summary>Sample rate in units of 100 Hz, as on the wire (480 for 48 kHz).</summary>
        public ushort SampleRateField;

        public byte Channels;
        public byte Format;

        public int SampleRateHz => SampleRateField * 100;
        public bool IsStart => (Flags & FlagStart) != 0;

        /// <summary>Frame size in bytes for pcm_s16le.</summary>
        public int FrameBytes => 2 * Channels;

        public static byte StreamTypeOf(string stream)
        {
            switch (stream)
            {
                case AudioStreams.Media: return StreamMedia;
                case AudioStreams.Alt: return StreamAlt;
                case AudioStreams.Telephony: return StreamTelephony;
                case "mic": return StreamMic;
                default: return 0;
            }
        }

        public static string StreamNameOf(byte streamType)
        {
            switch (streamType)
            {
                case StreamMedia: return AudioStreams.Media;
                case StreamAlt: return AudioStreams.Alt;
                case StreamTelephony: return AudioStreams.Telephony;
                case StreamMic: return "mic";
                default: return null;
            }
        }

        public bool Equals(AudioHeader other)
        {
            return Seq == other.Seq && StreamType == other.StreamType && Flags == other.Flags && Timestamp == other.Timestamp
                && SampleRateField == other.SampleRateField && Channels == other.Channels && Format == other.Format;
        }

        public override bool Equals(object obj)
        {
            return obj is AudioHeader && Equals((AudioHeader)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (int)(Seq * 31 + StreamType * 7 + Flags + Timestamp * 17 + SampleRateField * 13 + Channels * 3 + Format);
            }
        }

        public override string ToString()
        {
            return "seq " + Seq + " stream " + StreamType + " flags " + Flags + " ts " + Timestamp + " rate " + SampleRateHz
                + " ch " + Channels + " fmt " + Format;
        }
    }

    public static class AudioDatagram
    {
        /// <summary>Writes the header into <paramref name="buffer"/> at <paramref name="offset"/>.</summary>
        public static void WriteHeader(AudioHeader h, byte[] buffer, int offset)
        {
            buffer[offset + 0] = (byte)(h.Seq >> 8);
            buffer[offset + 1] = (byte)h.Seq;
            buffer[offset + 2] = h.StreamType;
            buffer[offset + 3] = h.Flags;
            buffer[offset + 4] = (byte)(h.Timestamp >> 24);
            buffer[offset + 5] = (byte)(h.Timestamp >> 16);
            buffer[offset + 6] = (byte)(h.Timestamp >> 8);
            buffer[offset + 7] = (byte)h.Timestamp;
            buffer[offset + 8] = (byte)(h.SampleRateField >> 8);
            buffer[offset + 9] = (byte)h.SampleRateField;
            buffer[offset + 10] = h.Channels;
            buffer[offset + 11] = h.Format;
        }

        /// <summary>A whole datagram: the header and the samples as interleaved s16 little-endian.</summary>
        public static byte[] Encode(AudioHeader h, short[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            var datagram = new byte[AudioHeader.Size + samples.Length * 2];
            WriteHeader(h, datagram, 0);
            for (var i = 0; i < samples.Length; i++)
            {
                datagram[AudioHeader.Size + 2 * i] = (byte)samples[i];
                datagram[AudioHeader.Size + 2 * i + 1] = (byte)(samples[i] >> 8);
            }
            return datagram;
        }

        /// <summary>Reads the header without checking it; false when there are fewer than 12 bytes.</summary>
        public static bool TryReadHeader(byte[] buffer, int offset, int length, out AudioHeader h)
        {
            h = default(AudioHeader);
            if (buffer == null || length < AudioHeader.Size || offset < 0 || offset + length > buffer.Length) return false;
            h.Seq = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            h.StreamType = buffer[offset + 2];
            h.Flags = buffer[offset + 3];
            h.Timestamp = ((uint)buffer[offset + 4] << 24) | ((uint)buffer[offset + 5] << 16) | ((uint)buffer[offset + 6] << 8) | buffer[offset + 7];
            h.SampleRateField = (ushort)((buffer[offset + 8] << 8) | buffer[offset + 9]);
            h.Channels = buffer[offset + 10];
            h.Format = buffer[offset + 11];
            return true;
        }

        /// <summary>
        /// Reads and checks a datagram from a tablet (spec §10.2): header fields valid for protocol 1 (stream 1-3,
        /// rate field 80-480, 1 or 2 channels, format pcm_s16le) and a payload of at least one whole frame, a multiple
        /// of the frame size, at most <see cref="AudioHeader.MaxPayloadBytes"/>. Returns null when valid, otherwise why not.
        /// </summary>
        public static string Validate(byte[] buffer, int offset, int length, out AudioHeader h)
        {
            return Validate(buffer, offset, length, global::RigPlayPlugin.Audio.AudioDirection.TabletToPc, out h);
        }

        /// <summary>
        /// As <see cref="Validate(byte[], int, int, out AudioHeader)"/> for a datagram flowing <paramref name="direction"/>:
        /// streamType 1-3 tablet to plugin, 4 (mic) plugin to tablet (spec §10.2, §10.4).
        /// </summary>
        public static string Validate(byte[] buffer, int offset, int length, global::RigPlayPlugin.Audio.AudioDirection direction, out AudioHeader h)
        {
            if (!TryReadHeader(buffer, offset, length, out h)) return "shorter than the 12-byte header";
            if (direction == global::RigPlayPlugin.Audio.AudioDirection.PcToTablet)
            {
                if (h.StreamType != AudioHeader.StreamMic) return "streamType " + h.StreamType + " does not flow plugin to tablet";
            }
            else if (h.StreamType < AudioHeader.StreamMedia || h.StreamType > AudioHeader.StreamTelephony)
            {
                return "invalid streamType " + h.StreamType;
            }
            if (h.Format != AudioHeader.FormatPcmS16Le) return "invalid format " + h.Format;
            if (h.Channels != 1 && h.Channels != 2) return "invalid channels " + h.Channels;
            if (h.SampleRateField < AudioHeader.MinSampleRateField || h.SampleRateField > AudioHeader.MaxSampleRateField)
                return "sampleRate field " + h.SampleRateField + " outside 80-480";
            var payload = length - AudioHeader.Size;
            if (payload < h.FrameBytes) return "no complete frame";
            if (payload % h.FrameBytes != 0) return "payload of " + payload + " bytes is not a whole number of frames";
            if (payload > AudioHeader.MaxPayloadBytes) return "payload of " + payload + " bytes exceeds " + AudioHeader.MaxPayloadBytes;
            return null;
        }

        /// <summary>Checks a datagram and returns its samples; throws <see cref="ProtocolException"/> when invalid.</summary>
        public static short[] Decode(byte[] datagram, out AudioHeader h)
        {
            return Decode(datagram, global::RigPlayPlugin.Audio.AudioDirection.TabletToPc, out h);
        }

        public static short[] Decode(byte[] datagram, global::RigPlayPlugin.Audio.AudioDirection direction, out AudioHeader h)
        {
            var problem = Validate(datagram, 0, datagram == null ? 0 : datagram.Length, direction, out h);
            if (problem != null) throw new ProtocolException(problem);
            var count = (datagram.Length - AudioHeader.Size) / 2;
            var samples = new short[count];
            for (var i = 0; i < count; i++)
            {
                samples[i] = (short)(datagram[AudioHeader.Size + 2 * i] | (datagram[AudioHeader.Size + 2 * i + 1] << 8));
            }
            return samples;
        }
    }
}
