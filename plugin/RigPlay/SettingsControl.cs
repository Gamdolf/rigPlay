// SPDX-License-Identifier: GPL-3.0-only
// SettingsControl.cs: the rigPlay page in SimHub's left menu, built in code (no XAML, see plugin/README.md).
// A header with the plugin version, then the Status, Pairing, Dashboards, Audio and Data sections. They are
// placeholders that show the stored settings; the later E2 tickets fill them (#20 status, #21 pairing,
// #23 dashboards, #24 audio; Data is v2). A change writes the settings object and saves it at once.
using System.Windows;
using System.Windows.Controls;

namespace RigPlayPlugin
{
    public class SettingsControl : UserControl
    {
        private readonly RigPlay plugin;

        public SettingsControl(RigPlay plugin)
        {
            this.plugin = plugin;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Content = BuildPage();
        }

        private UIElement BuildPage()
        {
            var page = Ui.VStack(16,
                BuildHeader(),
                BuildStatus(),
                BuildPairing(),
                BuildDashboards(),
                new global::RigPlayPlugin.Audio.AudioSection(plugin),
                BuildData());
            page.Margin = new Thickness(Theme.PagePadding);
            page.MaxWidth = Theme.PageMaxWidth;
            page.HorizontalAlignment = HorizontalAlignment.Left;

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = page,
            };
        }

        private FrameworkElement BuildHeader()
        {
            var title = Ui.HStack(10,
                Ui.Text("rigPlay", Theme.SizeTitle, FontWeights.SemiBold),
                Ui.Caption("plugin " + RigPlay.Version));
            return Ui.VStack(4,
                title,
                Ui.Caption("CarPlay tablets on your rig: discovery, pairing, the dashboard they show, and their audio on this PC."));
        }

        private FrameworkElement BuildStatus()
        {
            return new StatusSection(plugin).Build();
        }

        private FrameworkElement BuildPairing()
        {
            return new PairingSection(plugin).Build();
        }

        private FrameworkElement BuildDashboards()
        {
            return new DashboardSection(plugin).Build();
        }

        private static FrameworkElement BuildData()
        {
            return Ui.Section("Data (v2)",
                "SimHub telemetry sent to the tablet for its own widgets. Planned for a later version.");
        }
    }
}
