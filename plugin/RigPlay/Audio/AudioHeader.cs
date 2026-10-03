// SPDX-License-Identifier: GPL-3.0-only
// AudioHeader.cs: the 12-byte header of an audio datagram (docs/protocol.md §10.2), its codec and its
// validation. Header fields are big-endian, the PCM payload little-endian. Tested byte for byte against
// protocol/fixtures/audio-header.json. Pure: no SimHub, WPF or NAudio types (compiled into RigPlay.Tests and
// into plugin/tools/AudioSender).
using System;

namespace RigPlayPlugin.Audio
{
    /// <summary>The <c>streamType</c> header byte; also the <c>stream</c> member of audioStart / audioStop.</summary>
    public enum AudioStreamType : byte
    {
        /// <summary>Music and other media.</summary>
        Media = 1,
        /// <summary>Siri and other alternate audio.</summary>
        Alt = 2,
        /// <summary>Calls.</summary>
        Telephony = 3,
        /// <summary>The PC microphone (spec §10.4): flows plugin to tablet only, never tablet to plugin.</summary>
        Mic = 4,
    }

    /// <summary>
    /// Which way a datagram flows (spec §10.2): the streamType decides it. Tablet to plugin carries media, alt and
    /// telephony; plugin to tablet carries the microphone only.
    /// </summary>
    public enum AudioDirection
    {
        /// <summary>CarPlay audio the plugin receives: streamType 1-3.</summary>
        TabletToPc = 0,
        /// <summary>The PC microphone the tablet receives: streamType 4.</summary>
        PcToTablet = 1,
    }

    /// <summary>The <c>format</c> header byte.</summary>
    public enum AudioFormat : byte
    {
        PcmS16le = 1,
        /// <summary>Reserved in protocol 1.</summary>
        Opus = 2,
    }

    /// <summary>Why a datagram was rejected; <see cref="Ok"/> when it was not.</summary>
    public enum AudioHeaderError
    {
        Ok = 0,
        /// <summary>Shorter than the 12-byte header.</summary>
        TooShort,
        /// <summary>streamType 0 or 5-255.</summary>
        InvalidStreamType,
        /// <summary>streamType 4 (mic) from a tablet: the microphone flows plugin to tablet only.</summary>
        ReservedStreamType,
        /// <summary>format 0 or 3-255.</summary>
        InvalidFormat,
        /// <summary>format 2 (Opus), reserved in protocol 1.</summary>
        ReservedFormat,
        /// <summary>channels other than 1 or 2.</summary>
        InvalidChannels,
        /// <summary>sampleRate field outside 80-480.</summary>
        InvalidSampleRate,
        /// <summary>Header without any payload after it.</summary>
        NoPayload,
        /// <summary>Payload length not a positive multiple of 2 x channels (includes less than one frame).</summary>
        PartialFrame,
        /// <summary>streamType 1-3 read as plugin to tablet: only the microphone flows that way.</summary>
        WrongDirection,
    }

    public struct AudioHeader
    {
        public const int Size = 12;

        /// <summary>Flags bit 0: first datagram after audioStart.</summary>
        public const byte FlagStart = 0x01;

        public const int MinSampleRateField = 80;
        public const int MaxSampleRateField = 480;

        /// <summary>Receivers must accept payloads up to this size (§10.2).</summary>
        public const int MaxPayloadBytes = 8192;

        public ushort Seq;
        public AudioStreamType StreamType;
        public byte Flags;
        /// <summary>Sample frames sent on this stream before the first frame of this datagram.</summary>
        public uint Timestamp;
        /// <summary>The raw sampleRate field, in units of 100 Hz.</summary>
        public ushort SampleRateField;
        public byte Channels;
        public AudioFormat Format;

        public bool IsStart
        {
            get { return (Flags & FlagStart) != 0; }
        }

        public int SampleRate
        {
            get { return SampleRateField * 100; }
        }

        /// <summary>Bytes per frame of pcm_s16le: 2 x channels.</summary>
        public int BlockAlign
        {
            get { return 2 * Channels; }
        }

        public static AudioHeader Create(ushort seq, AudioStreamType streamType, bool start, uint timestamp, int sampleRateHz, int channels, AudioFormat format = AudioFormat.PcmS16le)
        {
            return new AudioHeader
            {
                Seq = seq,
                StreamType = streamType,
                Flags = start ? FlagStart : (byte)0,
                Timestamp = timestamp,
                SampleRateField = (ushort)(sampleRateHz / 100),
                Channels = (byte)channels,
                Format = format,
            };
        }

        /// <summary>
        /// Decodes and validates a datagram. On <see cref="AudioHeaderError.Ok"/> the payload is
        /// <c>data[offset + Size .. offset + length)</c>, a positive whole number of frames.
        /// Reserved flag bits are ignored, as §10.2 requires. The fields are filled in even when the result is an
        /// error, as far as the datagram is long enough, so that the error can be logged with them.
        /// </summary>
        public static AudioHeaderError TryParse(byte[] data, int offset, int length, out AudioHeader header)
        {
            return TryParse(data, offset, length, AudioDirection.TabletToPc, out header);
        }

        /// <summary>
        /// As <see cref="TryParse(byte[], int, int, out AudioHeader)"/>, for a datagram flowing <paramref name="direction"/>:
        /// tablet to plugin accepts streamType 1-3 (4 is <see cref="AudioHeaderError.ReservedStreamType"/>), plugin to
        /// tablet accepts 4 only (1-3 are <see cref="AudioHeaderError.WrongDirection"/>).
        /// </summary>
        public static AudioHeaderError TryParse(byte[] data, int offset, int length, AudioDirection direction, out AudioHeader header)
        {
            header = default(AudioHeader);
            if (data == null || length < Size || offset < 0 || offset + length > data.Length) return AudioHeaderError.TooShort;

            header.Seq = (ushort)((data[offset] << 8) | data[offset + 1]);
            header.StreamType = (AudioStreamType)data[offset + 2];
            header.Flags = data[offset + 3];
            header.Timestamp = ((uint)data[offset + 4] << 24) | ((uint)data[offset + 5] << 16) | ((uint)data[offset + 6] << 8) | data[offset + 7];
            header.SampleRateField = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
            header.Channels = data[offset + 10];
            header.Format = (AudioFormat)data[offset + 11];

            var type = data[offset + 2];
            if (type < 1 || type > 4) return AudioHeaderError.InvalidStreamType;
            if (direction == AudioDirection.TabletToPc && type == (byte)AudioStreamType.Mic) return AudioHeaderError.ReservedStreamType;
            if (direction == AudioDirection.PcToTablet && type != (byte)AudioStreamType.Mic) return AudioHeaderError.WrongDirection;
            var format = data[offset + 11];
            if (format == (byte)AudioFormat.Opus) return AudioHeaderError.ReservedFormat;
            if (format != (byte)AudioFormat.PcmS16le) return AudioHeaderError.InvalidFormat;
            if (header.Channels != 1 && header.Channels != 2) return AudioHeaderError.InvalidChannels;
            if (header.SampleRateField < MinSampleRateField || header.SampleRateField > MaxSampleRateField) return AudioHeaderError.InvalidSampleRate;

            var payload = length - Size;
            if (payload <= 0) return AudioHeaderError.NoPayload;
            if (payload % header.BlockAlign != 0) return AudioHeaderError.PartialFrame;
            return AudioHeaderError.Ok;
        }

        public static AudioHeaderError TryParse(byte[] data, out AudioHeader header)
        {
            return TryParse(data, 0, data == null ? 0 : data.Length, out header);
        }

        public static AudioHeaderError TryParse(byte[] data, AudioDirection direction, out AudioHeader header)
        {
            return TryParse(data, 0, data == null ? 0 : data.Length, direction, out header);
        }

        /// <summary>The direction a fixture vector names ("tabletToPc", the default, or "pcToTablet").</summary>
        public static AudioDirection ParseDirection(string name)
        {
            return name == "pcToTablet" ? AudioDirection.PcToTablet : AudioDirection.TabletToPc;
        }

        /// <summary>Writes the 12 header bytes at <paramref name="offset"/>.</summary>
        public void Write(byte[] buffer, int offset)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset + Size > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            buffer[offset] = (byte)(Seq >> 8);
            buffer[offset + 1] = (byte)Seq;
            buffer[offset + 2] = (byte)StreamType;
            buffer[offset + 3] = Flags;
            buffer[offset + 4] = (byte)(Timestamp >> 24);
            buffer[offset + 5] = (byte)(Timestamp >> 16);
            buffer[offset + 6] = (byte)(Timestamp >> 8);
            buffer[offset + 7] = (byte)Timestamp;
            buffer[offset + 8] = (byte)(SampleRateField >> 8);
            buffer[offset + 9] = (byte)SampleRateField;
            buffer[offset + 10] = Channels;
            buffer[offset + 11] = (byte)Format;
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[Size];
            Write(bytes, 0);
            return bytes;
        }

        /// <summary>A whole datagram: this header followed by the samples as s16 little-endian.</summary>
        public byte[] Encode(short[] samples, int sampleOffset = 0, int sampleCount = -1)
        {
            if (samples == null) samples = new short[0];
            if (sampleCount < 0) sampleCount = samples.Length - sampleOffset;
            var datagram = new byte[Size + 2 * sampleCount];
            Write(datagram, 0);
            for (var i = 0; i < sampleCount; i++)
            {
                var s = samples[sampleOffset + i];
                datagram[Size + 2 * i] = (byte)s;
                datagram[Size + 2 * i + 1] = (byte)(s >> 8);
            }
            return datagram;
        }

        /// <summary>The s16 little-endian samples of a payload.</summary>
        public static short[] DecodeSamples(byte[] data, int offset, int byteCount)
        {
            var samples = new short[byteCount / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(data[offset + 2 * i] | (data[offset + 2 * i + 1] << 8));
            }
            return samples;
        }

        /// <summary>The protocol name of a stream: media, alt, telephony, mic.</summary>
        public static string StreamName(AudioStreamType type)
        {
            switch (type)
            {
                case AudioStreamType.Media: return "media";
                case AudioStreamType.Alt: return "alt";
                case AudioStreamType.Telephony: return "telephony";
                case AudioStreamType.Mic: return "mic";
                default: return "unknown(" + (int)type + ")";
            }
        }

        /// <summary>Parses the <c>stream</c> member of audioStart / audioStop. Only media, alt and telephony.</summary>
        public static bool TryParseStreamName(string name, out AudioStreamType type)
        {
            switch (name)
            {
                case "media": type = AudioStreamType.Media; return true;
                case "alt": type = AudioStreamType.Alt; return true;
                case "telephony": type = AudioStreamType.Telephony; return true;
                default: type = 0; return false;
            }
        }

        public static string FormatName(AudioFormat format)
        {
            switch (format)
            {
                case AudioFormat.PcmS16le: return "pcm_s16le";
                case AudioFormat.Opus: return "opus";
                default: return "unknown(" + (int)format + ")";
            }
        }

        /// <summary>Parses the <c>format</c> member of audioStart. Protocol 1 accepts pcm_s16le only.</summary>
        public static bool TryParseFormatName(string name, out AudioFormat format)
        {
            if (name == "pcm_s16le")
            {
                format = AudioFormat.PcmS16le;
                return true;
            }
            format = 0;
            return false;
        }

        /// <summary>True for a sample rate audioStart may announce: a multiple of 100 Hz from 8000 to 48000.</summary>
        public static bool IsValidSampleRate(int hz)
        {
            return hz % 100 == 0 && hz / 100 >= MinSampleRateField && hz / 100 <= MaxSampleRateField;
        }

        public override string ToString()
        {
            return StreamName(StreamType) + " seq " + Seq + " ts " + Timestamp + " " + SampleRate + " Hz x" + Channels + " " + FormatName(Format) + (IsStart ? " start" : "");
        }
    }
}
