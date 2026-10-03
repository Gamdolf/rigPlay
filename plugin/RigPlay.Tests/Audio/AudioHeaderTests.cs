// SPDX-License-Identifier: GPL-3.0-only
// AudioHeaderTests.cs: the audio datagram codec against every vector in protocol/fixtures/audio-header.json
// (docs/protocol.md §10.2): decode, re-encode byte for byte, and reject every invalid datagram.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RigPlayPlugin.Audio;
using Xunit;

namespace RigPlayPlugin.Tests.Audio
{
    public class AudioHeaderTests
    {
        private static readonly Lazy<JObject> Fixture = new Lazy<JObject>(() => JObject.Parse(File.ReadAllText(FixturePath("audio-header.json"))));

        /// <summary>protocol/fixtures/&lt;name&gt;, found by walking up from the test binaries to the repository root.</summary>
        internal static string FixturePath(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "protocol", "fixtures", name);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("protocol/fixtures/" + name + " not found above " + AppContext.BaseDirectory);
        }

        private static byte[] Hex(string hex)
        {
            return Convert.FromHexString(hex);
        }

        private static string ToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static IEnumerable<object[]> ValidNames()
        {
            return Fixture.Value["valid"].Select(v => new object[] { (string)v["name"] });
        }

        public static IEnumerable<object[]> InvalidNames()
        {
            return Fixture.Value["invalid"].Select(v => new object[] { (string)v["name"] });
        }

        private static JToken Vector(string section, string name)
        {
            return Fixture.Value[section].Single(v => (string)v["name"] == name);
        }

        [Fact]
        public void FixtureDescribesTheSameLayout()
        {
            Assert.Equal(AudioHeader.Size, (int)Fixture.Value["headerSize"]);
            Assert.Equal("big-endian", (string)Fixture.Value["byteOrder"]["header"]);
            Assert.Equal("little-endian", (string)Fixture.Value["byteOrder"]["payload"]);
            Assert.Equal(4, Fixture.Value["valid"].Count());
            Assert.Equal(10, Fixture.Value["invalid"].Count());
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void DecodesValidVector(string name)
        {
            var v = Vector("valid", name);
            var h = v["header"];
            var datagram = Hex((string)v["datagramHex"]);

            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            Assert.Equal((int)h["seq"], header.Seq);
            Assert.Equal((int)h["streamType"], (int)header.StreamType);
            Assert.Equal((string)h["stream"], AudioHeader.StreamName(header.StreamType));
            Assert.Equal((int)h["flags"], header.Flags);
            Assert.Equal(((int)h["flags"] & 1) == 1, header.IsStart);
            Assert.Equal((uint)h["timestamp"], header.Timestamp);
            Assert.Equal((int)h["sampleRateField"], header.SampleRateField);
            Assert.Equal((int)h["sampleRateHz"], header.SampleRate);
            Assert.Equal((int)h["channels"], header.Channels);
            Assert.Equal((int)h["format"], (int)header.Format);

            var payloadLength = datagram.Length - AudioHeader.Size;
            Assert.Equal((int)v["frames"], payloadLength / header.BlockAlign);
            var samples = AudioHeader.DecodeSamples(datagram, AudioHeader.Size, payloadLength);
            Assert.Equal(v["samples"].Select(s => (short)(int)s).ToArray(), samples);
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void EncodesValidVectorByteForByte(string name)
        {
            var v = Vector("valid", name);
            var h = v["header"];
            var header = AudioHeader.Create(
                (ushort)(int)h["seq"],
                (AudioStreamType)(int)h["streamType"],
                ((int)h["flags"] & 1) == 1,
                (uint)h["timestamp"],
                (int)h["sampleRateHz"],
                (int)h["channels"],
                (AudioFormat)(int)h["format"]);

            Assert.Equal((string)v["headerHex"], ToHex(header.ToBytes()));
            var samples = v["samples"].Select(s => (short)(int)s).ToArray();
            Assert.Equal((string)v["datagramHex"], ToHex(header.Encode(samples)));
            Assert.Equal((string)v["payloadHex"], ToHex(header.Encode(samples).Skip(AudioHeader.Size).ToArray()));
        }

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void DecodeThenEncodeIsIdentity(string name)
        {
            var datagram = Hex((string)Vector("valid", name)["datagramHex"]);
            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            var samples = AudioHeader.DecodeSamples(datagram, AudioHeader.Size, datagram.Length - AudioHeader.Size);
            Assert.Equal(datagram, header.Encode(samples));
        }

        [Theory]
        [MemberData(nameof(InvalidNames))]
        public void RejectsInvalidVector(string name)
        {
            var datagram = Hex((string)Vector("invalid", name)["datagramHex"]);
            AudioHeader header;
            Assert.NotEqual(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
        }

        [Theory]
        [InlineData("too-short", AudioHeaderError.TooShort)]
        [InlineData("header-only", AudioHeaderError.NoPayload)]
        [InlineData("streamType-zero", AudioHeaderError.InvalidStreamType)]
        [InlineData("streamType-mic-reserved", AudioHeaderError.ReservedStreamType)]
        [InlineData("format-zero", AudioHeaderError.InvalidFormat)]
        [InlineData("format-opus-reserved", AudioHeaderError.ReservedFormat)]
        [InlineData("channels-three", AudioHeaderError.InvalidChannels)]
        [InlineData("sampleRate-zero", AudioHeaderError.InvalidSampleRate)]
        [InlineData("sampleRate-above-48k", AudioHeaderError.InvalidSampleRate)]
        [InlineData("payload-partial-frame", AudioHeaderError.PartialFrame)]
        public void RejectsInvalidVectorForItsReason(string name, AudioHeaderError expected)
        {
            var datagram = Hex((string)Vector("invalid", name)["datagramHex"]);
            AudioHeader header;
            Assert.Equal(expected, AudioHeader.TryParse(datagram, out header));
        }

        [Fact]
        public void IgnoresReservedFlagBits()
        {
            var datagram = Hex("000001fe0000000001e002010100ffff");
            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            Assert.False(header.IsStart);
            datagram[3] = 0xff;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out header));
            Assert.True(header.IsStart);
        }

        [Fact]
        public void AcceptsMaximumPayloadAndSampleRateBounds()
        {
            var header = AudioHeader.Create(1, AudioStreamType.Media, false, 0, 8000, 2);
            var datagram = header.Encode(new short[AudioHeader.MaxPayloadBytes / 2]);
            AudioHeader parsed;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(datagram, out parsed));
            Assert.Equal(8000, parsed.SampleRate);
            Assert.Equal(AudioHeader.MaxPayloadBytes / 4, (datagram.Length - AudioHeader.Size) / parsed.BlockAlign);
        }

        [Fact]
        public void ParsesAtAnOffset()
        {
            var datagram = Hex((string)Vector("valid", "alt-24k-mono")["datagramHex"]);
            var padded = new byte[datagram.Length + 5];
            Buffer.BlockCopy(datagram, 0, padded, 3, datagram.Length);
            AudioHeader header;
            Assert.Equal(AudioHeaderError.Ok, AudioHeader.TryParse(padded, 3, datagram.Length, out header));
            Assert.Equal(AudioStreamType.Alt, header.StreamType);
            Assert.Equal(AudioHeaderError.TooShort, AudioHeader.TryParse(padded, 3, padded.Length, out header));
        }

        [Theory]
        [InlineData("media", AudioStreamType.Media)]
        [InlineData("alt", AudioStreamType.Alt)]
        [InlineData("telephony", AudioStreamType.Telephony)]
        public void StreamNamesRoundTrip(string name, AudioStreamType type)
        {
            AudioStreamType parsed;
            Assert.True(AudioHeader.TryParseStreamName(name, out parsed));
            Assert.Equal(type, parsed);
            Assert.Equal(name, AudioHeader.StreamName(type));
        }

        [Fact]
        public void RejectsUnknownNamesAndRates()
        {
            AudioStreamType type;
            AudioFormat format;
            Assert.False(AudioHeader.TryParseStreamName("mic", out type));
            Assert.False(AudioHeader.TryParseStreamName("Media", out type));
            Assert.False(AudioHeader.TryParseFormatName("opus", out format));
            Assert.True(AudioHeader.TryParseFormatName("pcm_s16le", out format));
            Assert.True(AudioHeader.IsValidSampleRate(44100));
            Assert.True(AudioHeader.IsValidSampleRate(8000));
            Assert.True(AudioHeader.IsValidSampleRate(48000));
            Assert.False(AudioHeader.IsValidSampleRate(22050));
            Assert.False(AudioHeader.IsValidSampleRate(7900));
            Assert.False(AudioHeader.IsValidSampleRate(96000));
        }
    }
}
