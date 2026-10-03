// SPDX-License-Identifier: GPL-3.0-only
// PluginBridge.cs: the glue between SimHub and RigPlayHost. It points the pure code's log at SimHub's log, tells the
// host which SimHub it runs in, and starts and stops it with the plugin.
using System;
using System.Diagnostics;
using System.Reflection;
using SimHub.Plugins;

namespace RigPlayPlugin
{
    internal sealed class PluginBridge : IDisposable
    {
        private readonly RigPlay plugin;

        public PluginBridge(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        public RigPlayHost Host { get; private set; }

        public void Start(PluginManager pluginManager)
        {
            try
            {
                StartCore(pluginManager);
            }
            catch (Exception ex)
            {
                Log.Error("The tablet server could not start", ex);
            }
        }

        private void StartCore(PluginManager pluginManager)
        {
            PluginLog.Sink = Forward;
            var env = new HostEnvironment
            {
                PluginVersion = RigPlay.Version,
                SimHubVersion = DetectSimHubVersion(),
                MachineName = Environment.MachineName,
                SaveSettings = plugin.SaveSettings,
                SimHubDir = Dashboards.DashboardCatalog.FindSimHubDir(new[]
                {
                    AppDomain.CurrentDomain.BaseDirectory,
                    SafeDirectoryOf(typeof(PluginManager).Assembly),
                }),
                SimHubWebPort = DetectSimHubWebPort(),
            };
            Host = new RigPlayHost(plugin.Settings, env);
            Host.Start();
            Log.Info("Tablet server: control TCP " + (Host.Server.Status.Listening ? Host.Server.Port + " listening" : "not listening (" + Host.Server.Status.Error + ")")
                + ", beacon UDP " + plugin.Settings.DiscoveryPort + (Host.Beacon.Status.Running ? " running" : " not running")
                + ", host " + Host.DisplayName + " (" + plugin.Settings.HostId + "), SimHub " + (env.SimHubVersion ?? "?"));
        }

        public void Stop()
        {
            try
            {
                Host?.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Stopping the tablet server failed", ex);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static void Forward(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Debug: Log.Debug(message); break;
                case LogLevel.Info: Log.Info(message); break;
                case LogLevel.Warn: Log.Warn(message); break;
                default: Log.Error(message); break;
            }
        }

        private static string SafeDirectoryOf(Assembly assembly)
        {
            try { return System.IO.Path.GetDirectoryName(assembly.Location); } catch (Exception) { return null; }
        }

        /// <summary>
        /// SimHub keeps its web server port in its application settings (SimHubWPF.exe.config: SimHubWebPort). Read it
        /// from the loaded settings class by reflection; null when that fails (the page then uses 8888 or its override).
        /// </summary>
        private static int? DetectSimHubWebPort()
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = assembly.GetName().Name ?? "";
                    if (!name.StartsWith("SimHubWPF", StringComparison.OrdinalIgnoreCase)) continue;
                    Type[] types;
                    try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    foreach (var type in types)
                    {
                        if (type == null || type.Name != "Settings" || type.Namespace == null || !type.Namespace.EndsWith(".Properties")) continue;
                        var port = type.GetProperty("SimHubWebPort", BindingFlags.Public | BindingFlags.Instance);
                        var instance = type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                        if (port == null || instance == null) continue;
                        var value = Convert.ToInt32(port.GetValue(instance));
                        if (value > 0 && value <= 65535)
                        {
                            Log.Info("SimHub web dash server port from SimHub's settings: " + value);
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read SimHub's web server port: " + ex.Message);
            }
            return null;
        }

        /// <summary>SimHub's version as "9.12.6", from the SimHub executable; null when unknown.</summary>
        private static string DetectSimHubVersion()
        {
            try
            {
                var entry = Assembly.GetEntryAssembly();
                if (entry == null) return null;
                var info = FileVersionInfo.GetVersionInfo(entry.Location);
                if (info.FileMajorPart > 0 || info.FileMinorPart > 0)
                    return info.FileMajorPart + "." + info.FileMinorPart + "." + info.FileBuildPart;
                var v = entry.GetName().Version;
                return v == null ? null : v.Major + "." + v.Minor + "." + v.Build;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
