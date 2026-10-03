// SPDX-License-Identifier: GPL-3.0-only
// DashboardTests.cs: dashboard URLs (docs/protocol.md §11), DashTemplates enumeration and .metadata titles, the web
// dash probe, and state pushed to a paired fake tablet when the selection changes (#22).
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RigPlayPlugin.Dashboards;
using RigPlayPlugin.Protocol;
using Xunit;

namespace RigPlayPlugin.Tests
{
    public sealed class DashboardTests : IDisposable
    {
        private readonly string temp = Path.Combine(Path.GetTempPath(), "rigplay-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        [Theory]
        [InlineData("192.168.1.20", 8888, "Pit Board", "http://192.168.1.20:8888/dashboard/Pit%20Board")]
        [InlineData("::ffff:10.0.0.5", 8888, "Pit Board", "http://10.0.0.5:8888/dashboard/Pit%20Board")]
        [InlineData("127.0.0.1", 9999, "SimHub - FordGT", "http://127.0.0.1:9999/dashboard/SimHub%20-%20FordGT")]
        [InlineData("10.0.0.5", 8888, "a/b?c#d%e&f+g", "http://10.0.0.5:8888/dashboard/a%2Fb%3Fc%23d%25e%26f%2Bg")]
        [InlineData("10.0.0.5", 8888, "Nürburgring", "http://10.0.0.5:8888/dashboard/N%C3%BCrburgring")]
        [InlineData("fe80::1", 8888, "A", "http://[fe80::1]:8888/dashboard/A")]
        public void UrlsUseTheConnectionsLocalAddressAndEncodeTheName(string ip, int port, string name, string expected)
        {
            Assert.Equal(expected, DashboardUrls.Build(IPAddress.Parse(ip), port, name));
        }

        [Fact]
        public void NoDashboardMeansNoUrl()
        {
            Assert.Null(DashboardUrls.Build(IPAddress.Loopback, 8888, ""));
            Assert.Null(DashboardUrls.Build(IPAddress.Loopback, 8888, null));
        }

        [Fact]
        public void TheFixtureUrlIsWhatTheBuilderProduces()
        {
            var state = (StateMessage)MessageCodec.Decode(File.ReadAllText(Path.Combine(ProtocolFixturesTests.FixturesDir, "state.json")));
            Assert.Equal(state.DashboardUrl, DashboardUrls.Build(IPAddress.Parse("192.168.1.20"), 8888, "Pit Board"));
            Assert.Equal(state.IdleDashboardUrl, DashboardUrls.Build(IPAddress.Parse("192.168.1.20"), 8888, "Rig Clock"));
        }

        [Theory]
        [InlineData("{\"Category\":null,\"Title\":\"AIM GS-DASH\",\"Author\":\"Wotever\"}", "AIM GS-DASH")]
        [InlineData("{\"Category\":null,\"Title\":null,\"Author\":\"Wotever\"}", null)]
        [InlineData("{\"Title\":\"   \"}", null)]
        [InlineData("{\"Author\":\"x\"}", null)]
        [InlineData("{\"Title\":42}", null)]
        [InlineData("not json", null)]
        [InlineData("", null)]
        public void TheMetadataTitleIsReadWhenPresent(string json, string expected)
        {
            Assert.Equal(expected, DashboardCatalog.ReadTitle(json));
        }

        [Fact]
        public void DashTemplatesFoldersWithADjsonAreDashboards()
        {
            var dash = Path.Combine(temp, "DashTemplates");
            Make(dash, "AIM GS-DASH", "AIM GS-DASH.djson", "AIM GS-DASH.djson.metadata:{\"Title\":\"AIM GS-DASH\"}");
            Make(dash, "SimHub - FordGT", "SimHub - FordGT.djson", "Simhub - FordGT.djson.metadata:{\"Title\":\"Ford GT\"}");
            Make(dash, "MobileDash", "MobileDash.djson", "MobileDash.djson.metadata:{\"Title\":null}");
            Make(dash, "NoMeta", "NoMeta.djson");
            Make(dash, "_Library", "readme.txt");
            Make(dash, "Ressources");

            var list = DashboardCatalog.Enumerate(dash);
            Assert.Equal(new[] { "AIM GS-DASH", "Ford GT (SimHub - FordGT)", "MobileDash", "NoMeta" }, list.Select(d => d.Display).ToArray());
            Assert.Equal("SimHub - FordGT", list[1].Name);
            Assert.Equal("Ford GT", list[1].Title);
            Assert.Null(list[2].Title);

            Assert.Equal(temp, DashboardCatalog.FindSimHubDir(new[] { null, Path.Combine(temp, "nope"), temp }));
            Assert.Null(DashboardCatalog.FindSimHubDir(new[] { Path.Combine(temp, "nope") }));
            Assert.Empty(DashboardCatalog.Enumerate(Path.Combine(temp, "missing")));
        }

        private static void Make(string root, string folder, params string[] files)
        {
            var dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            foreach (var f in files)
            {
                var colon = f.IndexOf(':');
                File.WriteAllText(Path.Combine(dir, colon < 0 ? f : f.Substring(0, colon)), colon < 0 ? "{}" : f.Substring(colon + 1));
            }
        }

        // Web dash probe

        /// <summary>A minimal HTTP server on loopback that answers every request with 200.</summary>
        internal sealed class TinyHttpServer : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private volatile bool running = true;

            public TinyHttpServer()
            {
                listener.Start();
                Task.Run(Loop);
            }

            public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

            private async Task Loop()
            {
                while (running)
                {
                    try
                    {
                        using (var client = await listener.AcceptTcpClientAsync())
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[1024];
                            await stream.ReadAsync(buffer, 0, buffer.Length);
                            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                            await stream.WriteAsync(response, 0, response.Length);
                        }
                    }
                    catch (Exception)
                    {
                        if (!running) return;
                    }
                }
            }

            public void Dispose()
            {
                running = false;
                listener.Stop();
            }
        }

        private static int ClosedPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [Fact]
        public void TheProbeSeesAnHttpServerAndAClosedPort()
        {
            using (var http = new TinyHttpServer())
            {
                Assert.True(WebDashProbe.Probe(http.Port, 1000));
            }
            Assert.False(WebDashProbe.Probe(ClosedPort(), 1000));
        }

        [Fact]
        public void TheProbeReportsChanges()
        {
            using (var http = new TinyHttpServer())
            {
                var port = http.Port;
                var changes = 0;
                var probe = new WebDashProbe(() => port);
                probe.Changed += () => changes++;
                Assert.Null(probe.Current);
                probe.ProbeNow();
                Assert.True(probe.Current.Reachable);
                Assert.Equal(port, probe.Current.Port);
                probe.ProbeNow();
                Assert.Equal(1, changes);
                port = ClosedPort();
                probe.ProbeNow();
                Assert.False(probe.Current.Reachable);
                Assert.Equal(2, changes);
            }
        }

        // End to end

        [Fact]
        public void TheSelectionReachesAPairedTabletAsAUrlOnItsConnectionAddress()
        {
            var dash = Path.Combine(temp, "DashTemplates");
            Make(dash, "Pit Board", "Pit Board.djson");
            Make(dash, "Rig Clock", "Rig Clock.djson");
            using (var http = new TinyHttpServer())
            {
                var settings = new RigPlaySettings { SelectedDashboard = "Pit Board", WebDashPort = http.Port }.Normalize();
                var host = new RigPlayHost(settings, new HostEnvironment { PluginVersion = "0.1.0", SimHubDir = temp }, null, ControlServerTests.Fast)
                {
                    ControlPortOverride = 0,
                    BeaconEnabled = false,
                };
                host.Start();
                try
                {
                    Assert.Equal(new[] { "Pit Board", "Rig Clock" }, host.Dashboards.Select(d => d.Name).ToArray());
                    Assert.True(FakeTablet.WaitFor(() => host.Probe.Current != null && host.Probe.Current.Reachable));
                    host.Pairing.PinGenerator = () => "123456";
                    using (var t = new FakeTablet(host.Server.Port))
                    {
                        t.Hello();
                        t.Send(new PairRequestMessage());
                        t.Expect<PairResultMessage>();
                        t.Send(new PairRequestMessage { Pin = "123456" });
                        Assert.True(t.Expect<PairResultMessage>().Ok);
                        var state = t.Expect<StateMessage>();
                        Assert.Equal("http://127.0.0.1:" + http.Port + "/dashboard/Pit%20Board", state.DashboardUrl);
                        Assert.Null(state.IdleDashboardUrl);
                        Assert.True(state.DashboardServer.Reachable);
                        Assert.Equal(http.Port, state.DashboardServer.Port);

                        var before = DateTime.UtcNow;
                        settings.SelectedDashboard = "Rig Clock";
                        settings.IdleDashboard = "Pit Board";
                        host.DashboardSettingsChanged();
                        state = t.Expect<StateMessage>();
                        Assert.True((DateTime.UtcNow - before).TotalMilliseconds < 1000);
                        Assert.Equal("http://127.0.0.1:" + http.Port + "/dashboard/Rig%20Clock", state.DashboardUrl);
                        // The fake tablet named idleDashboard, so it gets the idle URL.
                        Assert.Equal("http://127.0.0.1:" + http.Port + "/dashboard/Pit%20Board", state.IdleDashboardUrl);

                        settings.SelectedDashboard = "";
                        host.DashboardSettingsChanged();
                        Assert.Null(t.Expect<StateMessage>().DashboardUrl);
                    }
                }
                finally
                {
                    host.Stop();
                }
            }
        }

        [Fact]
        public void TheWebDashPortFallsBackFromOverrideToSimHubToDefault()
        {
            var settings = new RigPlaySettings().Normalize();
            var env = new HostEnvironment();
            var host = new RigPlayHost(settings, env);
            Assert.Equal(8888, host.EffectiveWebDashPort);
            env.SimHubWebPort = 8890;
            Assert.Equal(8890, host.EffectiveWebDashPort);
            settings.WebDashPort = 9000;
            Assert.Equal(9000, host.EffectiveWebDashPort);
        }
    }
}
