// SPDX-License-Identifier: GPL-3.0-only
// RigPlaySettings.cs: everything the plugin remembers across SimHub restarts. SimHub serialises it with
// Newtonsoft.Json to PluginsData/Common/RigPlay.RigPlaySettings.json (ReadCommonSettings / SaveCommonSettings), so it
// is a plain class of public properties. Normalize() repairs whatever a hand-edited or older file contains.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;

namespace RigPlayPlugin
{
    public class RigPlaySettings
    {
        /// <summary>The shape of this file; bump it when a field changes meaning so Normalize can migrate.</summary>
        public const int CurrentSchemaVersion = 1;

        public const int MinVolume = 0;
        public const int MaxVolume = 100;
        public const int DefaultVolume = 100;
        public const string DefaultTabletName = "Tablet";

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>
        /// This installation's identity (spec §1): a lower-case RFC 4122 version 4 UUID, generated on first start and
        /// kept across restarts and IP changes. Tablets key their pairing token by it.
        /// </summary>
        public string HostId { get; set; } = NewHostId();

        /// <summary>The PC name shown on tablets; empty for the Windows computer name.</summary>
        public string HostName { get; set; } = "";

        public int ControlPort { get; set; } = ProtocolDefaults.ControlPort;

        /// <summary>The beacon port. Fixed by the protocol (spec §2): Normalize always resets it to the default.</summary>
        public int DiscoveryPort { get; set; } = ProtocolDefaults.DiscoveryPort;

        public int AudioPort { get; set; } = ProtocolDefaults.AudioPort;

        /// <summary>The SimHub dashboard pushed to tablets while a game is running; empty for none.</summary>
        public string SelectedDashboard { get; set; } = "";

        /// <summary>The dashboard shown while no game is running; empty to keep the selected one.</summary>
        public string IdleDashboard { get; set; } = "";

        /// <summary>The output device the tablet's audio plays on; empty for the Windows default device.</summary>
        public string AudioDeviceId { get; set; } = "";

        /// <summary>Playback volume, 0 to 100.</summary>
        public int Volume { get; set; } = DefaultVolume;

        public bool Muted { get; set; }

        public List<PairedTablet> PairedTablets { get; set; } = new List<PairedTablet>();

        /// <summary>
        /// Clamps and repairs every value in place, so the rest of the plugin can trust the object: ports in range
        /// and distinct, no null strings, volume in 0..100, and a tablet list without blanks or duplicates.
        /// </summary>
        public RigPlaySettings Normalize()
        {
            if (SchemaVersion < 1 || SchemaVersion > CurrentSchemaVersion) SchemaVersion = CurrentSchemaVersion;

            if (!IsValidHostId(HostId)) HostId = NewHostId();
            HostName = Clean(HostName);

            // The discovery port is not configurable: both sides always use 23710 (spec §2).
            DiscoveryPort = ProtocolDefaults.DiscoveryPort;
            ControlPort = ValidPort(ControlPort, ProtocolDefaults.ControlPort);
            AudioPort = ValidPort(AudioPort, ProtocolDefaults.AudioPort);
            if (ControlPort == DiscoveryPort || ControlPort == AudioPort || DiscoveryPort == AudioPort)
            {
                ControlPort = ProtocolDefaults.ControlPort;
                AudioPort = ProtocolDefaults.AudioPort;
            }

            SelectedDashboard = Clean(SelectedDashboard);
            IdleDashboard = Clean(IdleDashboard);
            AudioDeviceId = Clean(AudioDeviceId);

            Volume = Math.Min(MaxVolume, Math.Max(MinVolume, Volume));

            PairedTablets = (PairedTablets ?? new List<PairedTablet>())
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Token))
                .Select(t => t.Normalize())
                // One entry per tablet: a tablet that paired twice keeps its newest pairing.
                .GroupBy(t => t.Id, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(t => t.PairedAt).First())
                .OrderBy(t => t.PairedAt)
                .ToList();

            return this;
        }

        public PairedTablet FindTablet(string id)
        {
            return PairedTablets?.FirstOrDefault(t => t != null && string.Equals(t.Id, id, StringComparison.Ordinal));
        }

        /// <summary>A new host id: a lower-case, hyphenated random (version 4) UUID.</summary>
        public static string NewHostId()
        {
            return Guid.NewGuid().ToString("D").ToLowerInvariant();
        }

        /// <summary>True for a lower-case, hyphenated version 4 UUID.</summary>
        public static bool IsValidHostId(string value)
        {
            Guid parsed;
            if (string.IsNullOrEmpty(value) || value.Length != 36 || !Guid.TryParseExact(value, "D", out parsed)) return false;
            return string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) && value[14] == '4';
        }

        private static int ValidPort(int port, int fallback)
        {
            return port >= ProtocolDefaults.MinPort && port <= ProtocolDefaults.MaxPort ? port : fallback;
        }

        internal static string Clean(string value)
        {
            return value == null ? "" : value.Trim();
        }
    }

    /// <summary>A tablet that completed PIN pairing. The token is what it presents on every later connection.</summary>
    public class PairedTablet
    {
        /// <summary>Stable id the tablet generates once and keeps.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name, as the tablet reported it or the user renamed it.</summary>
        public string Name { get; set; } = "";

        /// <summary>Shared secret issued at pairing.</summary>
        public string Token { get; set; } = "";

        /// <summary>When pairing completed, in UTC.</summary>
        public DateTime PairedAt { get; set; }

        public PairedTablet Normalize()
        {
            Id = RigPlaySettings.Clean(Id);
            Token = RigPlaySettings.Clean(Token);
            Name = RigPlaySettings.Clean(Name);
            if (Name.Length == 0) Name = RigPlaySettings.DefaultTabletName;
            if (PairedAt.Kind == DateTimeKind.Local) PairedAt = PairedAt.ToUniversalTime();
            else if (PairedAt.Kind == DateTimeKind.Unspecified) PairedAt = DateTime.SpecifyKind(PairedAt, DateTimeKind.Utc);
            return this;
        }
    }
}
