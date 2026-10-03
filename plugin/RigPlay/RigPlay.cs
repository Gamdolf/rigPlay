// SPDX-License-Identifier: GPL-3.0-only
// RigPlay.cs: the SimHub plugin class. Reads and saves RigPlaySettings, and offers the rigPlay page in SimHub's
// left menu. Discovery, pairing, dashboard push and audio arrive with the later E2 tickets (#20-#25); this is
// the skeleton they hang off. No IDataPlugin: nothing here reads telemetry.
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

        public PluginManager PluginManager { get; set; }

        /// <summary>The live settings object; the page edits it and calls SaveSettings().</summary>
        public RigPlaySettings Settings { get; private set; } = new RigPlaySettings();

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
            // Writes the normalised file back, so a repaired or first-run file is on disk from the start.
            SaveSettings();
        }

        public void End(PluginManager pluginManager)
        {
            SaveSettings();
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
