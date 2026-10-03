// SPDX-License-Identifier: GPL-3.0-only
// TelemetryInput.cs: one frame of what the plugin reads from SimHub for the telemetry message (docs/protocol.md §6.9).
// RigPlay.DataUpdate fills it at 60 Hz from GameData.NewData without allocating; TelemetrySampler keeps the latest
// copy and feeds the GPS strategy with every frame. NaN (or null) means "the game does not publish it".
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
namespace RigPlayPlugin.Telemetry
{
    public struct TelemetryInput
    {
        /// <summary>SimHub reports a running game (GameData.GameRunning).</summary>
        public bool GameRunning;

        /// <summary>km/h; NaN when unknown.</summary>
        public double SpeedKmh;

        /// <summary>The sim gear as SimHub spells it: "R", "N", "1".."9", sometimes "P" or "D"; null when unknown.</summary>
        public string Gear;

        /// <summary>rev/min; NaN when unknown.</summary>
        public double Rpm;

        /// <summary>In the pit box (StatusDataBase.IsInPit).</summary>
        public bool InPit;

        /// <summary>Anywhere in the pit lane, box included (StatusDataBase.IsInPitLane).</summary>
        public bool InPitLane;

        /// <summary>Yaw in degrees as SimHub reports it (OrientationYaw); games without it report 0, see HeadingTracker.</summary>
        public double YawDeg;

        /// <summary>World coordinates in metres (CarCoordinates, axes game-dependent); NaN when unknown.</summary>
        public double X;
        public double Y;
        public double Z;

        /// <summary>SimHub saw the session restart (StatusDataBase.IsSessionRestart).</summary>
        public bool SessionRestart;

        /// <summary>Track (and layout) name; read about once a second, not per frame.</summary>
        public string TrackName;

        /// <summary>Session type ("Practice", "Race", ...); read about once a second.</summary>
        public string SessionType;

        /// <summary>A frame with nothing known: the state before the first DataUpdate, or with no game.</summary>
        public static TelemetryInput Empty => new TelemetryInput
        {
            SpeedKmh = double.NaN,
            Rpm = double.NaN,
            YawDeg = double.NaN,
            X = double.NaN,
            Y = double.NaN,
            Z = double.NaN,
        };
    }
}
