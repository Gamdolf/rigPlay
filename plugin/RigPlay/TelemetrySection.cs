// SPDX-License-Identifier: GPL-3.0-only
// TelemetrySection.cs: the "Data to CarPlay" section of the rigPlay page (#40, docs/protocol.md §6.9): the master
// switch, one switch per telemetry field, the fake-GPS strategy and its settings, and a live line saying what is sent
// and to whom. A change is saved at once and used by the next message (within 100 ms).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RigPlayPlugin.Telemetry;

namespace RigPlayPlugin
{
    internal sealed class TelemetrySection
    {
        private readonly RigPlay plugin;
        private readonly TextBlock statusText = Ui.Text("");
        private readonly TextBlock lastText = Ui.Caption("");
        private readonly StackPanel strategyRows = new StackPanel { Orientation = Orientation.Vertical };
        private ComboBox strategy;

        public TelemetrySection(RigPlay plugin)
        {
            this.plugin = plugin;
        }

        private TelemetrySettings Settings => plugin.Settings.Telemetry;

        private sealed class Choice
        {
            public string Name;
            public string Label;

            public override string ToString()
            {
                return Label;
            }
        }

        public FrameworkElement Build()
        {
            var master = Ui.Toggle(Settings.Enabled, on => Change("Telemetry " + (on ? "on" : "off"), () => Settings.Enabled = on));

            var fields = new UniformGrid { Columns = 3, HorizontalAlignment = HorizontalAlignment.Left };
            AddField(fields, "Speed", Settings.SendSpeed, v => Settings.SendSpeed = v);
            AddField(fields, "Gear (P/R/N/D)", Settings.SendGear, v => Settings.SendGear = v);
            AddField(fields, "Heading", Settings.SendHeading, v => Settings.SendHeading = v);
            AddField(fields, "Night mode", Settings.SendNight, v => Settings.SendNight = v);
            AddField(fields, "Fuel level", Settings.SendFuel, v => Settings.SendFuel = v);
            AddField(fields, "Range", Settings.SendRange, v => Settings.SendRange = v);
            AddField(fields, "RPM", Settings.SendRpm, v => Settings.SendRpm = v);
            AddField(fields, "Track name", Settings.SendTrackName, v => Settings.SendTrackName = v);
            AddField(fields, "Session type", Settings.SendSessionType, v => Settings.SendSessionType = v);

            strategy = new ComboBox { Width = 320 };
            var choices = GpsStrategies.All.Select(n => new Choice { Name = n, Label = GpsStrategies.Label(n) }).ToList();
            strategy.ItemsSource = choices;
            strategy.SelectedItem = choices.FirstOrDefault(c => c.Name == Settings.GpsStrategy) ?? choices[0];
            strategy.SelectionChanged += (s, e) => PageKit.Safe(() =>
            {
                var choice = strategy.SelectedItem as Choice;
                if (choice == null || choice.Name == Settings.GpsStrategy) return;
                Change("Telemetry position set to " + choice.Name, () => Settings.GpsStrategy = choice.Name);
                FillStrategyRows();
            });
            FillStrategyRows();

            var section = Ui.Section("Data to CarPlay",
                "SimHub data sent to the tablet while a game runs and an iPhone is connected (at most 10 times a second), so CarPlay "
                + "shows the car's speed and gear, a made-up GPS position for Maps, and night mode.",
                Ui.Row("Send SimHub data", master),
                Ui.Row("Fields", fields),
                Ui.Row("Position (fake GPS)", strategy),
                strategyRows,
                Ui.Row("Now", Ui.VStack(4, statusText, lastText)));
            PageKit.Live(section, 500, Refresh);
            return section;
        }

        private void AddField(UniformGrid grid, string label, bool value, Action<bool> set)
        {
            var toggle = Ui.Toggle(value, on => Change("Telemetry field " + label + " " + (on ? "on" : "off"), () => set(on)));
            var cell = Ui.HStack(8, toggle, Ui.Text(label));
            cell.Margin = new Thickness(0, 0, 24, 6);
            grid.Children.Add(cell);
        }

        /// <summary>The settings rows of the selected strategy (origin, reset rules); none for Off.</summary>
        private void FillStrategyRows()
        {
            strategyRows.Children.Clear();
            foreach (var row in StrategyRows(Settings.GpsStrategy))
            {
                row.Margin = new Thickness(0, 0, 0, 10);
                strategyRows.Children.Add(row);
            }
            strategyRows.Visibility = strategyRows.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private IEnumerable<FrameworkElement> StrategyRows(string name)
        {
            yield break;
        }

        private void Change(string what, Action apply)
        {
            apply();
            plugin.SaveSettings();
            Log.Info(what + " (Data to CarPlay)");
            Refresh();
        }

        private void Refresh()
        {
            var sender = plugin.Host?.TelemetrySender;
            if (sender == null)
            {
                statusText.Text = "Not running";
                lastText.Text = "";
                return;
            }
            statusText.Text = sender.Idle == null
                ? "Sending to " + sender.Targets + " tablet" + (sender.Targets == 1 ? "" : "s") + ", " + sender.MessagesSent + " messages so far"
                : "Not sending: " + sender.Idle;
            var last = sender.LastMessage;
            lastText.Text = last == null ? "" : "Last: " + (last.Length > 240 ? last.Substring(0, 240) + "…" : last);
        }
    }
}
