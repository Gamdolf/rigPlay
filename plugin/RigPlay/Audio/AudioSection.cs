// SPDX-License-Identifier: GPL-3.0-only
// AudioSection.cs: the Audio section of the rigPlay page (#24), built in code with the Ui helpers like the other
// sections: the output device picker (friendly names, refresh), the volume slider, mute, and the receiver's
// Opus toggle (off by default: offers tablets Opus before PCM, docs/protocol.md §10.4), and the receiver's
// live stats (port, output state, packets/s, loss, buffer depth and format per stream, last error), refreshed
// every 500 ms while the page is visible. Changes apply to the playing audio at once; the volume is saved
// shortly after the slider stops moving so dragging does not rewrite the settings file on every step.
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimHub.Plugins.Styles;

namespace RigPlayPlugin.Audio
{
    public class AudioSection : UserControl
    {
        private readonly RigPlay plugin;
        private readonly ComboBox devices = new ComboBox { MinWidth = 320, MaxWidth = 420 };
        private readonly TextBlock volumeText = Ui.Text("");
        private readonly TextBlock portText = Ui.Text("");
        private readonly TextBlock opusText = Ui.Text("");
        private readonly TextBlock outputText = Ui.Text("");
        private readonly TextBlock mediaText = Ui.Text("");
        private readonly TextBlock altText = Ui.Text("");
        private readonly TextBlock telephonyText = Ui.Text("");
        private readonly TextBlock totalsText = Ui.Text("");
        private readonly TextBlock errorText = Ui.Text("");
        private readonly DispatcherTimer statsTimer;
        private readonly DispatcherTimer saveTimer;
        private bool populating;

        private RigPlaySettings Settings => plugin.Settings;

        public AudioSection(RigPlay plugin)
        {
            this.plugin = plugin;

            statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AudioReceiver.StatsIntervalMs) };
            statsTimer.Tick += (s, e) => RefreshStats();
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += (s, e) =>
            {
                saveTimer.Stop();
                plugin.SaveSettings();
                Log.Info("Audio volume set to " + Settings.Volume + " % from the settings page");
            };

            Content = Build();
            Loaded += (s, e) =>
            {
                RefreshStats();
                statsTimer.Start();
            };
            Unloaded += (s, e) =>
            {
                statsTimer.Stop();
                if (saveTimer.IsEnabled)
                {
                    saveTimer.Stop();
                    plugin.SaveSettings();
                }
            };
        }

        private FrameworkElement Build()
        {
            devices.SelectionChanged += OnDeviceSelected;
            var refresh = SecondaryButton("Refresh", (s, e) => PopulateDevices());
            PopulateDevices();

            var slider = new Slider
            {
                Minimum = RigPlaySettings.MinVolume,
                Maximum = RigPlaySettings.MaxVolume,
                Value = Settings.Volume,
                Width = 260,
                SmallChange = 1,
                LargeChange = 10,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            volumeText.Text = Settings.Volume + " %";
            volumeText.MinWidth = 48;
            slider.ValueChanged += (s, e) =>
            {
                var v = (int)Math.Round(e.NewValue);
                if (v == Settings.Volume) return;
                Settings.Volume = v; // the output reads it on every buffer: applies at once
                volumeText.Text = v + " %";
                saveTimer.Stop();
                saveTimer.Start();
            };

            var mute = Ui.Toggle(Settings.Muted, on =>
            {
                Settings.Muted = on;
                plugin.SaveSettings();
                Log.Info("Audio " + (on ? "muted" : "unmuted") + " from the settings page");
            });

            var opus = Ui.Toggle(Settings.AudioOpus, on =>
            {
                Settings.AudioOpus = on;
                plugin.SaveSettings();
                Log.Info("Opus " + (on ? "enabled" : "disabled") + " from the settings page; tablets get the new format list");
                plugin.Audio?.ApplyOpus(); // pushes the new state.audio.formats to paired tablets
                RefreshOpusText();
            });
            RefreshOpusText();

            var port = PageKit.CommitTextBox(Settings.AudioPort.ToString(), 90, box =>
            {
                int value;
                if (!int.TryParse(box.Text, out value) || value < ProtocolDefaults.MinPort || value > ProtocolDefaults.MaxPort
                    || value == ProtocolDefaults.DiscoveryPort || value == Settings.ControlPort)
                {
                    box.Text = Settings.AudioPort.ToString();
                    return;
                }
                if (value == Settings.AudioPort) return;
                Settings.AudioPort = value;
                plugin.SaveSettings();
                Log.Info("Audio port changed to " + value + "; rebinding the audio receiver");
                plugin.Audio?.ApplyPort(); // pushes the new state.audio.port to paired tablets
                RefreshStats();
            });

            RefreshStats();
            return Ui.Section("Audio",
                "Plays the tablet's CarPlay audio (music, Siri, calls) on an output device of this PC. Siri and calls lower the music while they play.",
                Ui.Row("Output device", Ui.HStack(8, devices, refresh)),
                Ui.Row("Volume", Ui.HStack(12, slider, volumeText)),
                Ui.Row("Mute", mute),
                Ui.Row("Audio port (UDP)", Ui.HStack(12, port, portText)),
                Ui.Row("Opus compression", Ui.HStack(12, opus, opusText)),
                Ui.Row("Output", outputText),
                Ui.Row("Music and media", mediaText),
                Ui.Row("Siri", altText),
                Ui.Row("Calls", telephonyText),
                Ui.Row("Datagrams", totalsText),
                Ui.Row("Last error", errorText));
        }

        private void RefreshOpusText()
        {
            if (!Settings.AudioOpus)
            {
                opusText.Text = "Off: tablets send uncompressed PCM (about 1.5 Mbit/s). Turn on for a tablet on weak or shared Wi-Fi.";
                return;
            }
            opusText.Text = OpusSupport.Available
                ? "On: tablets send Opus (about 0.1 Mbit/s); the tablet falls back to PCM if it cannot encode."
                : "On, but unavailable: " + OpusSupport.UnavailableReason + ". Tablets send PCM.";
        }

        private void PopulateDevices()
        {
            populating = true;
            try
            {
                devices.Items.Clear();
                var list = ListDevicesSafe();
                string defaultName = null;
                foreach (var d in list)
                {
                    if (d.IsDefault) defaultName = d.Name;
                }
                devices.Items.Add(new ComboBoxItem { Content = defaultName == null ? "Windows default" : "Windows default (" + defaultName + ")", Tag = "" });
                var selected = 0;
                foreach (var d in list)
                {
                    devices.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
                    if (string.Equals(d.Id, Settings.AudioDeviceId, StringComparison.OrdinalIgnoreCase)) selected = devices.Items.Count - 1;
                }
                if (selected == 0 && !string.IsNullOrEmpty(Settings.AudioDeviceId))
                {
                    // The saved device is unplugged or disabled: keep it selected so it is used again when it returns.
                    devices.Items.Add(new ComboBoxItem { Content = "Saved device, not connected (Windows default plays)", Tag = Settings.AudioDeviceId });
                    selected = devices.Items.Count - 1;
                }
                if (list.Count == 0) devices.ToolTip = "No audio output device found on this PC";
                devices.SelectedIndex = selected;
            }
            catch (Exception ex)
            {
                Log.Warn("Could not fill the output device list: " + ex.Message);
            }
            finally
            {
                populating = false;
            }
        }

        private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
        {
            if (populating) return;
            var item = devices.SelectedItem as ComboBoxItem;
            if (item == null) return;
            var id = item.Tag as string ?? "";
            if (string.Equals(id, Settings.AudioDeviceId, StringComparison.Ordinal)) return;
            Settings.AudioDeviceId = id;
            plugin.SaveSettings();
            Log.Info("Audio output device set to " + (id.Length == 0 ? "the Windows default" : "\"" + item.Content + "\"") + " from the settings page");
            NotifyDeviceChanged();
        }

        private void RefreshStats()
        {
            var audio = plugin.Audio;
            var stats = audio?.Stats ?? AudioStats.Empty;
            if (audio == null)
            {
                portText.Text = "not started";
            }
            else
            {
                portText.Text = stats.Listening ? "listening on " + (stats.Port == 0 ? Settings.AudioPort : stats.Port) + ", paired tablets only" : "not listening (see Last error)";
            }
            outputText.Text = stats.Output;
            mediaText.Text = Line(stats, AudioStreamType.Media);
            altText.Text = Line(stats, AudioStreamType.Alt);
            telephonyText.Text = Line(stats, AudioStreamType.Telephony);
            totalsText.Text = stats.Datagrams + " received (" + Math.Round(stats.DatagramsPerSecond) + "/s) · "
                + stats.Invalid + " invalid · " + stats.Rejected + " rejected";
            errorText.Text = string.IsNullOrEmpty(stats.LastError) ? "None" : stats.LastError;
        }

        private static string Line(AudioStats stats, AudioStreamType type)
        {
            var s = stats.Find(type);
            return s == null ? "stopped" : s.ToDisplayString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static System.Collections.Generic.List<AudioDeviceInfo> ListDevicesSafe()
        {
            try
            {
                return AudioOutput.ListDevices();
            }
            catch (Exception ex)
            {
                Log.Warn("Audio devices cannot be listed: " + ex.Message);
                return new System.Collections.Generic.List<AudioDeviceInfo>();
            }
        }

        private void NotifyDeviceChanged()
        {
            try
            {
                plugin.Audio?.Output?.DeviceSettingChanged();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not switch the audio output device: " + ex.Message);
            }
        }

        private static Button SecondaryButton(string text, RoutedEventHandler click)
        {
            Button button;
            try
            {
                button = new SHButtonSecondary();
            }
            catch (Exception)
            {
                button = new Button();
                Ui.TryStyle(button, typeof(SHButtonSecondary));
            }
            button.Content = text;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Click += click;
            return button;
        }
    }
}
