// GCalendarSync - a Google Contacts CardDAV sync client for Windows Phone 8.1
// (and Windows 10 Mobile, which runs the same package).
// Copyright (C) 2026
//
// This program is free software; you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation; either version 2 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program; see the LICENSE file. If not, see <https://www.gnu.org/licenses/>.
//
// Whether the periodic download runs and how often, plus a way to make the next sync a full one.
using System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using GCalendarSync.Core.Storage;
using GCalendarSync.Core.Sync;

namespace GCalendarSync.Pages
{
    public sealed partial class SettingsPage : Page
    {
        // Set while the controls are being filled in from settings, so their change handlers do
        // not write the value straight back and re-register the background task on every visit.
        bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
            AutoSyncSwitch.IsOn = AppSettings.AutoSyncEnabled;
            SelectInterval(AppSettings.AutoSyncIntervalMinutes);

            PersonalSwitch.IsOn  = AppSettings.SyncPersonal;
            BirthdaysSwitch.IsOn = AppSettings.SyncBirthdays;
            HolidaysSwitch.IsOn  = AppSettings.SyncHolidays;
            OtherSwitch.IsOn     = AppSettings.SyncOther;
            SelectWindow(AppSettings.WindowMonthsBack);
            _loading = false;

            AboutText.Text =
                "GCalendarSync " + App.BuildVersion + "\n" +
                "Google Contacts over CardDAV, with a Google App Password. No Google Cloud " +
                "project and no OAuth client needed.\n" +
                "TLS is handled by a bundled OpenSSL build, because Windows Phone 8.1's own " +
                "certificate store predates the roots www.google.com uses today.";

            await ShowStateAsync();
        }

        async void OnAutoSyncToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.AutoSyncEnabled = AutoSyncSwitch.IsOn;
            await ApplyAsync();
        }

        /// <summary>
        /// All four calendar switches, which need no re-registration - only the next sync reads
        /// them. Turning one off is what makes the next sync delete that calendar from the phone.
        /// </summary>
        void OnCalendarToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.SyncPersonal  = PersonalSwitch.IsOn;
            AppSettings.SyncBirthdays = BirthdaysSwitch.IsOn;
            AppSettings.SyncHolidays  = HolidaysSwitch.IsOn;
            AppSettings.SyncOther     = OtherSwitch.IsOn;
        }

        void OnWindowChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            var item = WindowBox.SelectedItem as ComboBoxItem;
            if (item == null) return;

            int months;
            if (!int.TryParse(item.Tag as string, out months)) return;

            // One control for both halves: "three months either side" is what a person means,
            // and two separate pickers would be a setting nobody wants to think about twice.
            AppSettings.WindowMonthsBack = months;
            AppSettings.WindowMonthsForward = months;
        }

        void SelectWindow(int months)
        {
            foreach (var entry in WindowBox.Items)
            {
                var item = entry as ComboBoxItem;
                if (item == null) continue;

                int value;
                if (int.TryParse(item.Tag as string, out value) && value == months)
                {
                    WindowBox.SelectedItem = item;
                    return;
                }
            }
            WindowBox.SelectedIndex = 1;   // 3 months
        }

        async void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            var item = IntervalBox.SelectedItem as ComboBoxItem;
            if (item == null) return;

            uint minutes;
            if (!uint.TryParse(item.Tag as string, out minutes)) return;

            AppSettings.AutoSyncIntervalMinutes = minutes;
            await ApplyAsync();
        }

        async void OnResetClick(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog(
                "Forget the collection tags and etags? The next sync downloads the whole window " +
                "from Google again. Events on the phone are kept until that sync replaces them, " +
                "and the account stays signed in.",
                "forget sync state");
            dialog.Commands.Add(new UICommand("forget"));
            dialog.Commands.Add(new UICommand("cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;

            var chosen = await dialog.ShowAsync();
            if (chosen == null || chosen.Label != "forget") return;

            await SyncState.ClearAsync();
            AppSettings.LastDownload = null;
            await ShowStateAsync();
        }

        async System.Threading.Tasks.Task ApplyAsync()
        {
            string problem = await BackgroundSync.ApplyAsync();
            await ShowStateAsync();

            if (problem != null)
                await new MessageDialog(problem, "automatic sync").ShowAsync();
        }

        async System.Threading.Tasks.Task ShowStateAsync()
        {
            var state = await SyncState.LoadAsync();

            StateText.Text =
                (state.HasSynced
                    ? state.Etags.Count + " events tracked in " + state.Calendars.Count + " calendar(s)"
                    : "nothing synced yet - the first sync will be a full download") + "\n" +
                "last sync: " + (AppSettings.LastDownload ?? "never") + "\n" +
                "automatic sync scheduled: " + (BackgroundSync.IsRegistered ? "yes" : "no");
        }

        void SelectInterval(uint minutes)
        {
            foreach (var entry in IntervalBox.Items)
            {
                var item = entry as ComboBoxItem;
                if (item == null) continue;

                uint value;
                if (uint.TryParse(item.Tag as string, out value) && value == minutes)
                {
                    IntervalBox.SelectedItem = item;
                    return;
                }
            }
            // A stored interval with no matching entry (an older build, say) falls back to the
            // shortest the platform allows.
            if (IntervalBox.Items.Count > 0) IntervalBox.SelectedIndex = 0;
        }
    }
}
