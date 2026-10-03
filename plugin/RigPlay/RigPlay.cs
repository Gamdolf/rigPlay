// SPDX-License-Identifier: GPL-3.0-only
// RigPlay.cs: the SimHub plugin class. Reads and saves RigPlaySettings, offers the rigPlay page in SimHub's left
// menu, starts the tablet server (PluginBridge: discovery, pairing, dashboards, SimHub surface) and the audio
// pipeline, and connects the two (AudioGlue). No IDataPlugin: nothing here reads telemetry.
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimHub.Plugins;

namespace RigPlayPlugin
{
    [PluginName("rigPlay")]
    [PluginAuthor("xorob0")]
    [PluginDescription("Pairs rigPlay CarPlay tablets with SimHub, picks the dashboard they show and plays their audio on this PC.")]
    public class RigPlay : IPlugin, IWPFSettingsV2
    {
        /// <summary>SimHub stores the settings as PluginsData/Common/RigPlay.RigPlaySettings.json.</summary>
        public const string SettingsKey = "RigPlaySettings";

        public const string IconResource = "RigPlay.Icon.png";

        private ImageSource icon;
        private bool iconLoaded;
        private PluginBridge bridge;
        private global::RigPlayPlugin.Audio.AudioGlue audioGlue;

        /// <summary>Discovery, control server and tablet state; null before Init and after End.</summary>
        public RigPlayHost Host => bridge?.Host;

        public PluginManager PluginManager { get; set; }

        /// <summary>The live settings object; the page edits it and calls SaveSettings().</summary>
        public RigPlaySettings Settings { get; private set; } = new RigPlaySettings();

        /// <summary>The audio receiver and output (#24); the control server forwards audioStart / audioStop to it.</summary>
        public global::RigPlayPlugin.Audio.AudioPipeline Audio { get; private set; }

        public string LeftMenuTitle => "rigPlay";

        public ImageSource PictureIcon
        {
            get
            {
                if (!iconLoaded)
                {
                    iconLoaded = true;
                    icon = LoadIcon();
                }
                return icon;
            }
        }

        /// <summary>The plugin version from Directory.Build.props, e.g. "0.1.0".</summary>
        public static string Version
        {
            get
            {
                var assembly = typeof(RigPlay).Assembly;
                var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .Select(a => a.InformationalVersion)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(informational))
                {
                    var plus = informational.IndexOf('+');
                    return plus > 0 ? informational.Substring(0, plus) : informational;
                }
                var version = assembly.GetName().Version;
                return version == null ? "0.0.0" : version.Major + "." + version.Minor + "." + version.Build;
            }
        }

        public void Init(PluginManager pluginManager)
        {
            Log.Info("rigPlay plugin " + Version + " starting");
            LoadSettings();
            Log.Info("Settings loaded: control port " + Settings.ControlPort + ", discovery port " + Settings.DiscoveryPort
                + ", audio port " + Settings.AudioPort + ", " + Settings.PairedTablets.Count + " paired tablet(s)");
            bridge = new PluginBridge(this);
            bridge.Start(pluginManager);
            // Writes the normalised file back, so a repaired or first-run file is on disk from the start.
            SaveSettings();
            Audio = new global::RigPlayPlugin.Audio.AudioPipeline(() => Settings);
            Audio.LearnedBufferChanged += SaveSettings; // the depth learned from the network survives a restart
            AttachAudio();
        }

        /// <summary>
        /// Audio plays only for paired tablets: datagrams from other sources are dropped and a stream starts only with
        /// its audioStart. state.audio.enabled is true while the audio port is bound, even with no output device (the
        /// page then shows why nothing plays). Without a tablet server no source is accepted.
        /// </summary>
        private void AttachAudio()
        {
            try
            {
                var host = Host;
                if (host == null)
                {
                    Audio.Receiver.AutoStartOnFirstFlag = false;
                    Audio.Receiver.SourceFilter = address => false;
                    Log.Warn("Audio is received from no tablet: the tablet server did not start");
                    return;
                }
                audioGlue = new global::RigPlayPlugin.Audio.AudioGlue(host, Audio.Receiver);
                Audio.PortChanged += audioGlue.ListenerChanged;
                Audio.FormatsChanged += audioGlue.ListenerChanged;
                Log.Info("Audio receiver wired to the tablet server: " + (Audio.Receiver.Listening ? "UDP " + Audio.Receiver.BoundPort + ", paired tablets only" : "not listening, tablets play locally"));
            }
            catch (Exception ex)
            {
                Log.Error("Connecting the audio receiver to the tablet server failed", ex);
            }
        }

        public void End(PluginManager pluginManager)
        {
            audioGlue?.Dispose();
            audioGlue = null;
            Audio?.Dispose();
            SaveSettings();
            bridge?.Stop();
            Log.Info("rigPlay plugin stopped");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            try
            {
                return new SettingsControl(this);
            }
            catch (Exception ex)
            {
                Log.Error("The settings page could not be built", ex);
                return new UserControl
                {
                    Content = new TextBlock
                    {
                        Text = "The rigPlay page could not be displayed: " + ex.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(24),
                    },
                };
            }
        }

        public void SaveSettings()
        {
            try
            {
                Settings.Normalize();
                this.SaveCommonSettings(SettingsKey, Settings);
            }
            catch (Exception ex)
            {
                Log.Error("Saving the settings failed", ex);
            }
        }

        private void LoadSettings()
        {
            try
            {
                Settings = this.ReadCommonSettings<RigPlaySettings>(SettingsKey, () => new RigPlaySettings()) ?? new RigPlaySettings();
            }
            catch (Exception ex)
            {
                Log.Error("Reading the settings failed; using the defaults", ex);
                Settings = new RigPlaySettings();
            }
            Settings.Normalize();
        }

        private static ImageSource LoadIcon()
        {
            try
            {
                using (var stream = typeof(RigPlay).Assembly.GetManifestResourceStream(IconResource))
                {
                    if (stream == null)
                    {
                        Log.Warn("The menu icon resource " + IconResource + " is missing");
                        return null;
                    }
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    frame.Freeze();
                    return frame;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not load the menu icon: " + ex.Message);
                return null;
            }
        }
    }
}
