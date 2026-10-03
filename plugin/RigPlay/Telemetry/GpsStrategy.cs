// SPDX-License-Identifier: GPL-3.0-only
// GpsStrategy.cs: how the telemetry message gets a position (docs/protocol.md §6.9 lat/lon/alt). Sims do not publish
// GPS coordinates, so a strategy makes one up from the settings and the car's motion; the strategy combo on the
// page lists GpsStrategies.All. HeadingTracker turns the sim's yaw (or, without yaw, the car's movement) into a
// compass heading the strategies and the heading field share.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;

namespace RigPlayPlugin.Telemetry
{
    /// <summary>A WGS 84 position.</summary>
    public struct GpsFix
    {
        public double Lat;
        public double Lon;
        public double Alt;
    }

    /// <summary>
    /// Makes up a position. <see cref="Update"/> runs on SimHub's data thread for every frame (60 Hz) and must not
    /// allocate; <see cref="TryGetFix"/> runs at 10 Hz. TelemetrySampler serialises the calls.
    /// </summary>
    public interface IGpsStrategy
    {
        /// <param name="input">This frame.</param>
        /// <param name="headingDeg">Compass heading from HeadingTracker; NaN when unknown.</param>
        /// <param name="dtSec">Seconds since the previous frame (0 for the first frame after a gap).</param>
        void Update(ref TelemetryInput input, double headingDeg, double dtSec);

        /// <summary>The current position; false while there is none.</summary>
        bool TryGetFix(out GpsFix fix);

        /// <summary>Back to the start (the origin), as after a session start.</summary>
        void Reset();
    }

    /// <summary>The strategy names stored in TelemetrySettings.GpsStrategy and shown in the page's combo.</summary>
    public static class GpsStrategies
    {
        /// <summary>No position: lat/lon/alt are not sent.</summary>
        public const string Off = "off";

        /// <summary>Every strategy, in the order the page lists them.</summary>
        public static readonly string[] All = { Off };

        public static bool IsKnown(string name)
        {
            return name != null && Array.IndexOf(All, name) >= 0;
        }

        /// <summary>What the page shows for a strategy.</summary>
        public static string Label(string name)
        {
            switch (name)
            {
                case Off: return "Off (no position)";
                default: return name;
            }
        }

        /// <summary>A new strategy for <paramref name="settings"/>; null for <see cref="Off"/> or an unknown name.</summary>
        public static IGpsStrategy Create(TelemetrySettings settings)
        {
            switch (settings?.GpsStrategy)
            {
                default: return null;
            }
        }

        /// <summary>
        /// Everything a strategy is built from, as one string: when it changes, the sampler builds a new strategy
        /// (so a changed origin moves the car back to it at once).
        /// </summary>
        public static string Key(TelemetrySettings settings)
        {
            return settings == null ? Off : settings.GpsStrategy;
        }
    }

    /// <summary>
    /// The car's compass heading. The sim's yaw (OrientationYaw, degrees) when the game publishes it; games that do not
    /// report 0 forever, so yaw counts as published once a frame shows a non-zero value. Without yaw, the direction of
    /// the last movement of at least <see cref="MinMoveM"/> in the world coordinates (X, Z; game-dependent axes).
    /// </summary>
    public sealed class HeadingTracker
    {
        public const double MinMoveM = 1.0;

        private bool yawSeen;
        private bool anchored;
        private double anchorX;
        private double anchorZ;

        /// <summary>Degrees clockwise from north, 0 ≤ h &lt; 360; NaN while unknown.</summary>
        public double Heading { get; private set; } = double.NaN;

        public void Reset()
        {
            yawSeen = false;
            anchored = false;
            Heading = double.NaN;
        }

        public void Update(ref TelemetryInput input)
        {
            var yaw = input.YawDeg;
            if (!double.IsNaN(yaw) && !double.IsInfinity(yaw) && Math.Abs(yaw) > 1e-6) yawSeen = true;
            if (yawSeen && !double.IsNaN(yaw) && !double.IsInfinity(yaw))
            {
                Heading = Normalize(yaw);
                return;
            }
            if (double.IsNaN(input.X) || double.IsNaN(input.Z) || double.IsInfinity(input.X) || double.IsInfinity(input.Z)) return;
            if (!anchored)
            {
                anchorX = input.X;
                anchorZ = input.Z;
                anchored = true;
                return;
            }
            var dx = input.X - anchorX;
            var dz = input.Z - anchorZ;
            if (dx * dx + dz * dz < MinMoveM * MinMoveM) return;
            Heading = Normalize(Math.Atan2(dx, dz) * 180.0 / Math.PI);
            anchorX = input.X;
            anchorZ = input.Z;
        }

        /// <summary>Any angle in degrees to 0 ≤ h &lt; 360.</summary>
        public static double Normalize(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return double.NaN;
            var h = degrees % 360.0;
            if (h < 0) h += 360.0;
            if (h >= 360.0) h -= 360.0;
            return h;
        }
    }
}
