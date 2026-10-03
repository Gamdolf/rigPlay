// SPDX-License-Identifier: GPL-3.0-only
// ControlServerTests.cs: the control channel end to end with an in-process fake tablet over real TCP on loopback:
// hello/welcome and version negotiation (spec §7), heartbeats and the watchdog (§9), one session per tablet (§12),
// malformed and unexpected lines (§14), state after pairing (§6.6), shutdown (§13.3), and bind failures (§2).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using RigPlayPlugin.Net;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public sealed class ControlServerTests : IDisposable
    {
        public const string HostId = "3f6c2a4e-8d1b-4c7a-9e55-0b2d7f1a6c90";

        /// <summary>Fast timings so the liveness tests take well under a second each.</summary>
        public static SessionTimings Fast => new SessionTimings
        {
            HeartbeatMs = 100,
            WatchdogMs = 600,
            HelloTimeoutMs = 600,
            PairRequestTimeoutMs = 800,
            TickMs = 20,
            ErrorIntervalMs = 0,
        };

        private readonly ControlServer server;
        private readonly List<FakeTablet> tablets = new List<FakeTablet>();

        public ControlServerTests()
        {
            server = NewServer(Fast);
            server.Pairing = new TokenPairing();
            server.StateFactory = s => new StateMessage { DashboardUrl = null, Audio = new AudioInfo { Enabled = false, Port = 23712 } };
            Assert.True(server.Start(), server.Status.Error);
        }

        public static ControlServer NewServer(SessionTimings timings)
        {
            return new ControlServer(0, () => new WelcomeMessage { HostId = HostId, Name = "RIG-PC", Version = "0.1.0", SimhubVersion = "9.12.6" }, timings);
        }

        public void Dispose()
        {
            foreach (var t in tablets) t.Dispose();
            server.Stop();
        }

        private FakeTablet Connect()
        {
            var t = new FakeTablet(server.Port);
            tablets.Add(t);
            return t;
        }

        /// <summary>Pairs on token "good", refuses anything else.</summary>
        private sealed class TokenPairing : IPairingAuthority
        {
            public PairResultMessage HandlePairRequest(ClientSession session, PairRequestMessage request)
            {
                return request.Token == "good" ? PairResultMessage.Success("good") : PairResultMessage.Failure(PairReasons.TokenInvalid);
            }

            public void SessionClosed(ClientSession session) { }
        }

        [Fact]
        public void HelloIsAnsweredWithWelcome()
        {
            var t = Connect();
            var welcome = t.Hello();
            Assert.Equal(HostId, welcome.HostId);
            Assert.Equal("RIG-PC", welcome.Name);
            Assert.Equal("0.1.0", welcome.Version);
            Assert.Equal("9.12.6", welcome.SimhubVersion);
            Assert.Equal(1, welcome.Protocol);
            // The plugin offers idleDashboard only; the tablet named telemetry and idleDashboard.
            Assert.Equal(new[] { Features.IdleDashboard }, welcome.Features);
            Assert.True(FakeTablet.WaitFor(() => server.Sessions.Any(s => s.State == SessionState.Unpaired && s.TabletName == "Lenovo Tab P11")));
        }

        [Fact]
        public void AHandTypedHelloWithCarriageReturnWorks()
        {
            var t = Connect();
            t.SendLine("");
            t.SendLine("{\"type\":\"hello\",\"tabletId\":\"nc\",\"name\":\"nc\",\"appVersion\":\"0\",\"protocol\":1}\r");
            Assert.Empty(t.Expect<WelcomeMessage>().Features);
        }

        [Fact]
        public void AFirstLineThatIsNotHelloIsFatal()
        {
            var t = Connect();
            t.SendLine("{\"type\":\"heartbeat\",\"seq\":0}");
            Assert.True(t.ExpectError(ErrorCodes.HelloRequired).IsFatal);
            t.ExpectClosed();
        }

        [Fact]
        public void AnInvalidHelloIsFatal()
        {
            var t = Connect();
            t.SendLine("{\"type\":\"hello\",\"tabletId\":\"x\",\"name\":\"n\",\"appVersion\":\"1\",\"protocol\":2,\"minProtocol\":3}");
            t.ExpectError(ErrorCodes.HelloRequired);
            t.ExpectClosed();
        }

        [Fact]
        public void NoCommonVersionIsUnsupportedProtocol()
        {
            var t = Connect();
            t.Send(FakeTablet.NewHello(protocol: 3, minProtocol: 2));
            var error = t.ExpectError(ErrorCodes.UnsupportedProtocol);
            Assert.True(error.IsFatal);
            Assert.Equal(1, error.MinProtocol);
            Assert.Equal(1, error.MaxProtocol);
            t.ExpectClosed();
        }

        [Fact]
        public void ANewerTabletIsNegotiatedDownToOne()
        {
            var t = Connect();
            t.Send(FakeTablet.NewHello(protocol: 4, minProtocol: 1));
            Assert.Equal(1, t.Expect<WelcomeMessage>().Protocol);
        }

        [Fact]
        public void NoHelloWithinTheTimeoutCloses()
        {
            var t = Connect();
            t.ExpectClosed();
            Assert.True(FakeTablet.WaitFor(() => server.Sessions.Count == 0));
        }

        [Fact]
        public void ThePluginSendsHeartbeatsAfterWelcome()
        {
            var t = Connect();
            t.Hello();
            t.Send(new PairRequestMessage { Token = "good" });
            t.Expect<PairResultMessage>();
            t.Expect<StateMessage>();
            for (var i = 0; i < 4; i++)
            {
                t.Send(new HeartbeatMessage { Seq = i });
                var line = t.ReadLine();
                Assert.NotNull(line);
            }
            Assert.Contains(t.Received, l => l.StartsWith("{\"type\":\"heartbeat\",\"seq\":"));
        }

        [Fact]
        public void HeartbeatLossDropsTheSession()
        {
            var closed = new List<ClientSession>();
            server.SessionClosed += s => { lock (closed) closed.Add(s); };
            var t = Connect();
            t.Hello();
            t.Send(new PairRequestMessage { Token = "good" });
            Assert.True(t.Expect<PairResultMessage>().Ok);
            t.Expect<StateMessage>();
            Assert.Single(server.PairedSessions);

            // The tablet goes silent: the plugin keeps sending heartbeats, then closes after the watchdog.
            var started = DateTime.UtcNow;
            t.ExpectClosed();
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            Assert.InRange(elapsed, 400, 3000);
            Assert.True(t.HeartbeatsReceived >= 3);
            Assert.True(FakeTablet.WaitFor(() => server.PairedSessions.Count == 0));
            lock (closed) Assert.Contains(closed, s => s.CloseReason.StartsWith("link lost"));
        }

        [Fact]
        public void HeartbeatsKeepTheSessionOpenPastTheWatchdog()
        {
            var t = Connect();
            t.Hello();
            t.Send(new PairRequestMessage { Token = "good" });
            t.Expect<PairResultMessage>();
            t.Expect<StateMessage>();
            var until = DateTime.UtcNow.AddMilliseconds(1200);
            var seq = 0;
            while (DateTime.UtcNow < until)
            {
                t.Send(new HeartbeatMessage { Seq = seq++ });
                Assert.NotNull(t.ReadLine());
            }
            Assert.Single(server.PairedSessions);
        }

        [Fact]
        public void ASecondConnectionFromTheSameTabletReplacesTheFirst()
        {
            var first = Connect();
            first.Hello();
            var second = Connect();
            second.Send(FakeTablet.NewHello());
            Assert.True(first.ExpectError(ErrorCodes.Replaced).IsFatal);
            first.ExpectClosed();
            second.Expect<WelcomeMessage>();
            Assert.True(FakeTablet.WaitFor(() => server.Sessions.Count == 1));
        }

        [Fact]
        public void TwoDifferentTabletsCoexist()
        {
            var a = Connect();
            a.Hello("tablet-a");
            var b = Connect();
            b.Hello("tablet-b");
            a.Send(new PairRequestMessage { Token = "good" });
            b.Send(new PairRequestMessage { Token = "good" });
            Assert.True(a.Expect<PairResultMessage>().Ok);
            Assert.True(b.Expect<PairResultMessage>().Ok);
            Assert.True(FakeTablet.WaitFor(() => server.PairedSessions.Count == 2));
        }

        [Fact]
        public void AMalformedLineIsIgnoredAndTheSessionGoesOn()
        {
            var t = Connect();
            t.Hello();
            t.SendLine("this is not json");
            var error = t.ExpectError(ErrorCodes.BadMessage);
            Assert.False(error.IsFatal);
            t.SendLine("{\"type\":\"selfDestruct\"}");
            t.Send(new PairRequestMessage { Token = "good" });
            // The unknown type got no reply: the next message is the pairResult.
            Assert.True(t.Expect<PairResultMessage>().Ok);
        }

        [Fact]
        public void AnInvalidKnownMessageIsAnsweredWithBadMessage()
        {
            var t = Connect();
            t.Hello();
            t.SendLine("{\"type\":\"pairRequest\",\"pin\":\"123\"}");
            var error = t.ExpectError(ErrorCodes.BadMessage);
            Assert.Equal(MessageTypes.PairRequest, error.RefType);
        }

        [Fact]
        public void StatusBeforePairingIsNotPairedAndNoStateIsSent()
        {
            var t = Connect();
            t.Hello();
            t.Send(new StatusMessage { PhoneConnected = false, Screen = Screens.Idle });
            var error = t.ExpectError(ErrorCodes.NotPaired);
            Assert.Equal(MessageTypes.Status, error.RefType);
            server.BroadcastState();
            t.Send(new PairRequestMessage { Token = "bad" });
            Assert.Equal(PairReasons.TokenInvalid, t.Expect<PairResultMessage>().Reason);
        }

        [Fact]
        public void PairingSendsStateAndChangesAreBroadcastOnce()
        {
            var url = "http://127.0.0.1:8888/dashboard/A";
            string current = null;
            server.StateFactory = s => new StateMessage { DashboardUrl = current, Audio = new AudioInfo { Enabled = false, Port = 23712 } };
            var t = Connect();
            t.Hello();
            t.Send(new PairRequestMessage { Token = "good" });
            t.Expect<PairResultMessage>();
            Assert.Null(t.Expect<StateMessage>().DashboardUrl);

            server.BroadcastState(); // unchanged: nothing sent
            current = url;
            server.BroadcastState();
            Assert.Equal(url, t.Expect<StateMessage>().DashboardUrl);
        }

        [Fact]
        public void HelloAndPairRequestWhenPairedAreUnexpected()
        {
            var t = Connect();
            t.Hello();
            t.Send(new PairRequestMessage { Token = "good" });
            t.Expect<PairResultMessage>();
            t.Expect<StateMessage>();
            t.Send(new PairRequestMessage());
            Assert.Equal(MessageTypes.PairRequest, t.ExpectError(ErrorCodes.UnexpectedMessage).RefType);
            t.Send(FakeTablet.NewHello());
            Assert.Equal(MessageTypes.Hello, t.ExpectError(ErrorCodes.UnexpectedMessage).RefType);
            t.SendLine("{\"type\":\"state\",\"dashboardUrl\":null,\"audio\":{\"enabled\":false,\"port\":1,\"formats\":[]}}");
            t.ExpectError(ErrorCodes.UnexpectedMessage);
        }

        [Fact]
        public void StatusIsRecordedAndAudioMessagesAreRaised()
        {
            StatusMessage got = null;
            AudioStartMessage start = null;
            server.StatusReceived += (s, m) => got = m;
            server.AudioStartReceived += (s, m) => start = m;
            var t = Connect();
            t.Hello();
            t.Send(new PairRequestMessage { Token = "good" });
            t.Expect<PairResultMessage>();
            t.Expect<StateMessage>();
            t.SendLine(System.IO.File.ReadAllText(System.IO.Path.Combine(ProtocolFixturesTests.FixturesDir, "status.json")).Replace("\n", "").Replace("\r", ""));
            t.SendLine("{\"type\":\"audioStart\",\"stream\":\"media\",\"format\":\"pcm_s16le\",\"sampleRate\":48000,\"channels\":2}");
            Assert.True(FakeTablet.WaitFor(() => got != null && start != null));
            Assert.Equal("Teardrop", got.NowPlaying.Title);
            var session = server.PairedSessions.Single();
            Assert.True(session.PhoneConnectedOrder > 0);
            Assert.Equal(48000, start.SampleRate);
        }

        [Fact]
        public void NoPairRequestWithinTheTimeoutCloses()
        {
            var t = Connect();
            t.Hello();
            var until = DateTime.UtcNow.AddMilliseconds(2500);
            var closedAt = DateTime.MaxValue;
            // Keep the watchdog happy; only the missing pairRequest should close the session.
            while (DateTime.UtcNow < until)
            {
                try
                {
                    t.Send(new HeartbeatMessage());
                    if (t.ReadLine() == null) { closedAt = DateTime.UtcNow; break; }
                }
                catch (Exception)
                {
                    closedAt = DateTime.UtcNow;
                    break;
                }
            }
            Assert.True(closedAt != DateTime.MaxValue, "the session was not closed");
        }

        [Fact]
        public void ATooLongLineIsFatal()
        {
            var t = Connect();
            t.Hello();
            t.SendLine("{\"type\":\"heartbeat\",\"pad\":\"" + new string('x', ProtocolDefaults.MaxLineBytes) + "\"}");
            Assert.True(t.ExpectError(ErrorCodes.LineTooLong).IsFatal);
            t.ExpectClosed();
        }

        [Fact]
        public void AFatalErrorFromTheTabletClosesTheSession()
        {
            var t = Connect();
            t.Hello();
            t.Send(ErrorMessage.Of(ErrorCodes.Shutdown, "app exiting", true));
            t.ExpectClosed();
            Assert.True(FakeTablet.WaitFor(() => server.Sessions.Count == 0));
        }

        [Fact]
        public void StopSaysShutdownToEveryone()
        {
            var a = Connect();
            a.Hello("tablet-a");
            var b = Connect();
            b.Hello("tablet-b");
            Assert.True(FakeTablet.WaitFor(() => server.Sessions.Count(s => s.State == SessionState.Unpaired) == 2));
            server.Stop();
            Assert.True(a.ExpectError(ErrorCodes.Shutdown).IsFatal);
            a.ExpectClosed();
            Assert.True(b.ExpectError(ErrorCodes.Shutdown).IsFatal);
            b.ExpectClosed();
            Assert.False(server.Status.Listening);
        }

        [Fact]
        public void APortInUseIsReportedNotThrown()
        {
            var other = new ControlServer(server.Port, () => new WelcomeMessage(), Fast);
            Assert.False(other.Start());
            Assert.False(other.Status.Listening);
            Assert.Contains(server.Port.ToString(), other.Status.Error);
            Assert.Contains("already in use", other.Status.Error);
            other.Stop();
        }
    }
}
