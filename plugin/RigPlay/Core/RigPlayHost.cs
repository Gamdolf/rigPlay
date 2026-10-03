// SPDX-License-Identifier: GPL-3.0-only
// RigPlayHost.cs: everything rigPlay runs inside SimHub, without SimHub: the discovery beacon, the control server and
// the per-session state. The plugin class creates one in Init and disposes it in End; the page reads it.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.Linq;
using RigPlayPlugin.Net;
using RigPlayPlugin.Pairing;
using RigPlayPlugin.Protocol;

namespace RigPlayPlugin
{
    /// <summary>What the host needs from its surroundings.</summary>
    public sealed class HostEnvironment
    {
        public string PluginVersion { get; set; } = "0.0.0";
        public string SimHubVersion { get; set; }
        public string MachineName { get; set; } = Environment.MachineName;

        /// <summary>Persists the settings object (SimHub's SaveCommonSettings).</summary>
        public Action SaveSettings { get; set; } = () => { };
    }

    public sealed class RigPlayHost : IDisposable
    {
        private readonly object sync = new object();
        private readonly HostEnvironment env;
        private readonly IClock clock;
        private bool started;

        public RigPlayHost(RigPlaySettings settings, HostEnvironment env, IClock clock = null, SessionTimings timings = null)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.env = env ?? new HostEnvironment();
            this.clock = clock ?? SystemClock.Instance;
            Timings = timings ?? SessionTimings.Default;
            Pairing = new PairingService(settings, () => this.env.SaveSettings(), this.clock);
            Pairing.Changed += RaiseChanged;
        }

        /// <summary>PINs, tokens and the paired-tablet list (spec §8).</summary>
        public PairingService Pairing { get; }

        public RigPlaySettings Settings { get; }

        public SessionTimings Timings { get; }

        public ControlServer Server { get; private set; }

        public DiscoveryBeacon Beacon { get; private set; }

        /// <summary>
        /// Whether the audio receiver is running and plays what tablets send (state.audio.enabled, spec §6.6). The audio
        /// receiver (#24) sets this; until it does, tablets are told to play locally.
        /// </summary>
        public Func<bool> AudioEnabled { get; set; } = () => false;

        /// <summary>Tests: listen on this port instead of Settings.ControlPort (0 picks a free one).</summary>
        public int? ControlPortOverride { get; set; }

        /// <summary>Tests turn the beacon off so they do not broadcast on the LAN.</summary>
        public bool BeaconEnabled { get; set; } = true;

        /// <summary>Something the page shows changed (sessions, status, server state). Raised on any thread.</summary>
        public event Action Changed;

        /// <summary>The name tablets show for this PC.</summary>
        public string DisplayName => string.IsNullOrEmpty(Settings.HostName) ? env.MachineName : Settings.HostName;

        /// <summary>Id of the primary tablet's session (spec §12); 0 when there is none.</summary>
        public int PrimarySessionId => 0;

        public string PluginVersion => env.PluginVersion;

        public string SimHubVersion => env.SimHubVersion;

        public void Start()
        {
            lock (sync)
            {
                if (started) return;
                started = true;
            }
            StartServer();
            Beacon = new DiscoveryBeacon(BuildBeacon);
            if (BeaconEnabled) Beacon.Start();
            RaiseChanged();
        }

        public void Stop()
        {
            lock (sync)
            {
                if (!started) return;
                started = false;
            }
            try { Beacon?.Stop(); } catch (Exception ex) { PluginLog.Error("Stopping the beacon failed", ex); }
            try { Server?.Stop(); } catch (Exception ex) { PluginLog.Error("Stopping the control server failed", ex); }
            RaiseChanged();
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Restarts the control server, e.g. after the control port changed. Sessions get `shutdown`.</summary>
        public void RestartServer()
        {
            try { Server?.Stop("control port changed"); } catch (Exception ex) { PluginLog.Error("Stopping the control server failed", ex); }
            StartServer();
            RaiseChanged();
        }

        /// <summary>Sends every Paired session its state if it changed.</summary>
        public void PushState()
        {
            Server?.BroadcastState();
        }

        public BeaconMessage BuildBeacon()
        {
            return new BeaconMessage
            {
                Name = DisplayName,
                HostId = Settings.HostId,
                Version = env.PluginVersion,
                SimhubVersion = env.SimHubVersion,
                ControlPort = Settings.ControlPort,
                AudioPort = Settings.AudioPort,
                Protocol = ProtocolDefaults.ProtocolVersion,
                MinProtocol = ProtocolDefaults.MinProtocolVersion,
            };
        }

        public WelcomeMessage BuildWelcomeTemplate()
        {
            return new WelcomeMessage
            {
                HostId = Settings.HostId,
                Name = DisplayName,
                Version = env.PluginVersion,
                SimhubVersion = env.SimHubVersion,
            };
        }

        /// <summary>The state for one Paired session (spec §6.6).</summary>
        public StateMessage BuildState(ClientSession session)
        {
            bool audio;
            try { audio = AudioEnabled != null && AudioEnabled(); } catch (Exception) { audio = false; }
            return new StateMessage
            {
                DashboardUrl = null,
                Audio = new AudioInfo { Enabled = audio, Port = Settings.AudioPort, Formats = new List<string> { AudioStreams.PcmS16Le } },
            };
        }

        /// <summary>Deny on the page: discards the pending PIN and tells the tablet `denied` (spec §8 step 4).</summary>
        public void DenyPairing(string tabletId)
        {
            if (!Pairing.Deny(tabletId)) return;
            var session = Server?.FindByTabletId(tabletId);
            if (session == null || session.State != SessionState.Unpaired) return;
            session.Send(PairResultMessage.Failure(PairReasons.Denied));
            session.RestartPairRequestTimer();
        }

        /// <summary>Forget on the page: deletes the token hash and closes the tablet's live session with `forgotten`.</summary>
        public void ForgetTablet(string tabletId)
        {
            var known = Pairing.Forget(tabletId);
            var session = Server?.FindByTabletId(tabletId);
            if (session != null && (known || session.State == SessionState.Paired))
                session.CloseWithError(ErrorCodes.Forgotten, "this tablet was removed on the PC");
            RaiseChanged();
        }

        private void StartServer()
        {
            var server = new ControlServer(ControlPortOverride ?? Settings.ControlPort, BuildWelcomeTemplate, Timings, clock)
            {
                StateFactory = BuildState,
                Pairing = Pairing,
            };
            server.SessionsChanged += RaiseChanged;
            Server = server;
            server.Start();
        }

        internal void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { PluginLog.Error("A change handler failed", ex); }
        }
    }
}
