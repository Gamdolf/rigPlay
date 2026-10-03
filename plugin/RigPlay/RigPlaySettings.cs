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
        /// <remarks>2: protocol ports (23711/23712), host id, token hashes instead of tokens. 3: Telemetry (#40).</remarks>
        public const int CurrentSchemaVersion = 3;

        /// <summary>Placeholder defaults of schema 1, migrated by Normalize.</summary>
        internal const int LegacyControlPort = 18877;
        internal const int LegacyAudioPort = 18879;

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

        /// <summary>
        /// The dashboard (folder name under DashTemplates) the tablet shows for the SimHub button in CarPlay
        /// (state.dashboardUrl); empty for none.
        /// </summary>
        public string SelectedDashboard { get; set; } = "";

        /// <summary>The dashboard shown while no iPhone is connected (state.idleDashboardUrl); empty for the tablet's home screen.</summary>
        public string IdleDashboard { get; set; } = "";

        /// <summary>SimHub's web dash server port; 0 to use SimHub's own setting (8888 unless changed there).</summary>
        public int WebDashPort { get; set; }

        /// <summary>The output device the tablet's audio plays on; empty for the Windows default device.</summary>
        public string AudioDeviceId { get; set; } = "";

        /// <summary>Playback volume, 0 to 100.</summary>
        public int Volume { get; set; } = DefaultVolume;

        public bool Muted { get; set; }

        public List<PairedTablet> PairedTablets { get; set; } = new List<PairedTablet>();

        /// <summary>The "Data to CarPlay" section: what goes into the telemetry message (spec §6.9).</summary>
        public TelemetrySettings Telemetry { get; set; } = new TelemetrySettings();

        /// <summary>
        /// Clamps and repairs every value in place, so the rest of the plugin can trust the object: ports in range
        /// and distinct, no null strings, volume in 0..100, and a tablet list without blanks or duplicates.
        /// </summary>
        public RigPlaySettings Normalize()
        {
            if (SchemaVersion == 1)
            {
                // Schema 1 (plugin skeleton) used placeholder ports before docs/protocol.md fixed them: move files
                // that still carry those defaults to the protocol's.
                if (ControlPort == LegacyControlPort) ControlPort = ProtocolDefaults.ControlPort;
                if (AudioPort == LegacyAudioPort) AudioPort = ProtocolDefaults.AudioPort;
                SchemaVersion = CurrentSchemaVersion;
            }
            // 2 -> 3 only added Telemetry, which a file without it gets from its default.
            if (SchemaVersion < 1 || SchemaVersion > CurrentSchemaVersion || SchemaVersion == 2) SchemaVersion = CurrentSchemaVersion;

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

            if (WebDashPort < 0 || WebDashPort > ProtocolDefaults.MaxPort) WebDashPort = 0;

            SelectedDashboard = Clean(SelectedDashboard);
            IdleDashboard = Clean(IdleDashboard);
            AudioDeviceId = Clean(AudioDeviceId);

            Volume = Math.Min(MaxVolume, Math.Max(MinVolume, Volume));

            Telemetry = (Telemetry ?? new TelemetrySettings()).Normalize();

            PairedTablets = (PairedTablets ?? new List<PairedTablet>())
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id))
                .Select(t => t.Normalize())
                // A tablet without a valid token hash could never resume; drop it rather than show a dead entry.
                .Where(t => PairedTablet.IsTokenHash(t.TokenHash))
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

    /// <summary>
    /// The "Data to CarPlay" section (#40): a master switch, one switch per telemetry field, and the fake-GPS strategy
    /// that produces lat/lon/alt. gameRunning is always sent: it is how the tablet learns that the game stopped.
    /// </summary>
    public class TelemetrySettings
    {
        /// <summary>Master switch: nothing is sent while it is off.</summary>
        public bool Enabled { get; set; } = true;

        public bool SendSpeed { get; set; } = true;
        public bool SendGear { get; set; } = true;
        public bool SendHeading { get; set; } = true;
        public bool SendNight { get; set; } = true;
        public bool SendFuel { get; set; } = true;
        public bool SendRange { get; set; } = true;
        public bool SendRpm { get; set; } = true;
        public bool SendTrackName { get; set; } = true;
        public bool SendSessionType { get; set; } = true;

        /// <summary>How lat/lon/alt are made up (<see cref="Telemetry.GpsStrategies"/>); "off" sends no position.</summary>
        public string GpsStrategy { get; set; } = global::RigPlayPlugin.Telemetry.GpsStrategies.Off;

        /// <summary>Default origin: the Nürburgring, a place every sim racer knows.</summary>
        public const double DefaultOriginLat = 50.3356;
        public const double DefaultOriginLon = 6.9475;
        public const double DefaultOriginAlt = 617.0;

        /// <summary>The origin of the fake GPS (#42): latitude in degrees, WGS 84.</summary>
        public double OriginLat { get; set; } = DefaultOriginLat;

        /// <summary>Longitude in degrees, WGS 84.</summary>
        public double OriginLon { get; set; } = DefaultOriginLon;

        /// <summary>Altitude in metres above mean sea level.</summary>
        public double OriginAlt { get; set; } = DefaultOriginAlt;

        /// <summary>True when at least one data field would be sent (spec §6.9: nothing is sent otherwise).</summary>
        public bool AnyFieldEnabled()
        {
            return SendSpeed || SendGear || SendHeading || SendNight || SendFuel || SendRange || SendRpm || SendTrackName || SendSessionType
                || GpsStrategy != global::RigPlayPlugin.Telemetry.GpsStrategies.Off;
        }

        public TelemetrySettings Normalize()
        {
            if (!global::RigPlayPlugin.Telemetry.GpsStrategies.IsKnown(GpsStrategy)) GpsStrategy = global::RigPlayPlugin.Telemetry.GpsStrategies.Off;
            if (!Finite(OriginLat) || OriginLat < -90 || OriginLat > 90 || !Finite(OriginLon) || OriginLon < -180 || OriginLon > 180)
            {
                OriginLat = DefaultOriginLat;
                OriginLon = DefaultOriginLon;
            }
            if (!Finite(OriginAlt) || OriginAlt < -1000 || OriginAlt > 10000) OriginAlt = DefaultOriginAlt;
            return this;
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>A tablet that completed PIN pairing. The token is what it presents on every later connection.</summary>
    public class PairedTablet
    {
        /// <summary>Stable id the tablet generates once and keeps.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display name, as the tablet reported it or the user renamed it.</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// SHA-256 of the token issued at pairing, lower-case hex. The token itself is never stored (spec §15); the
        /// tablet presents it on every later connection and the plugin compares hashes.
        /// </summary>
        public string TokenHash { get; set; } = "";

        /// <summary>When pairing completed, in UTC.</summary>
        public DateTime PairedAt { get; set; }

        public PairedTablet Normalize()
        {
            Id = RigPlaySettings.Clean(Id);
            TokenHash = RigPlaySettings.Clean(TokenHash).ToLowerInvariant();
            Name = RigPlaySettings.Clean(Name);
            if (Name.Length == 0) Name = RigPlaySettings.DefaultTabletName;
            if (PairedAt.Kind == DateTimeKind.Local) PairedAt = PairedAt.ToUniversalTime();
            else if (PairedAt.Kind == DateTimeKind.Unspecified) PairedAt = DateTime.SpecifyKind(PairedAt, DateTimeKind.Utc);
            return this;
        }

        /// <summary>64 lower-case hex digits.</summary>
        public static bool IsTokenHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
