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
// The account, the two sync buttons and what the last sync did. All the work is SyncEngine's;
// this page only collects credentials, shows progress and reports the outcome.
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using GCalendarSync.Core.Storage;
using GCalendarSync.Core.Sync;

namespace GCalendarSync
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();
            VersionText.Text = "version " + App.BuildVersion;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var saved = CredentialStore.Load();
            if (saved != null)
            {
                EmailBox.Text = saved.Email;
                PasswordBox.Password = saved.Password;
            }

            // Documents\caldav.cfg, if it is there, without being asked. Putting a file of
            // credentials in Documents is a deliberate act, so it outranks whatever was saved
            // before - which is the whole point when the file is how an account gets swapped for
            // testing. Nothing happens when the file is absent, which is the ordinary case.
            var fromFile = await CredentialsFileReader.TryLoadFromDocumentsAsync();
            if (fromFile != null && fromFile.IsComplete && fromFile.Problem == null)
            {
                Apply(fromFile);
                CredentialStore.Save(fromFile.Email, fromFile.Password);
                saved = CredentialStore.Load();
            }

            RefreshStatus();

            // Keep the background registration in step with the settings on every visit: the
            // user may have changed the interval, and a registration is lost if the app is
            // reinstalled.
            if (saved != null) await BackgroundSync.ApplyAsync();
            RefreshStatus();
        }

        // ================================================================
        // Google -> phone
        // ================================================================
        async void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            string email, password;
            if (!ReadAccount(out email, out password)) return;

            SetBusy(true);
            try
            {
                var outcome = await SyncEngine.DownloadAsync(
                    email, password, ProgressReporter(), CancellationToken.None);

                Show(outcome);

                // Only arm the timer once a download has actually worked: the background task
                // gives up when there is no sync state, and an unusable App Password should not
                // be retried unattended every fifteen minutes.
                if (outcome.Success) await BackgroundSync.ApplyAsync();
            }
            finally
            {
                SetBusy(false);
                RefreshStatus();
            }
        }

        // ================================================================
        // App bar
        // ================================================================
        void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Pages.SettingsPage));
        }

        // ================================================================
        // Load the account from a file, so a 16-character App Password does
        // not have to be typed on a phone keyboard to test something
        // ================================================================
        async void OnLoadCredentialsClick(object sender, RoutedEventArgs e)
        {
            HideBanners();

            // Documents first, because it needs no interaction at all when the file is where the
            // app expects it.
            var fromDocuments = await CredentialsFileReader.TryLoadFromDocumentsAsync();
            if (fromDocuments != null)
            {
                Apply(fromDocuments);
                return;
            }

            // Not there, so let the user point at it. WP8.1 suspends the app while the picker is on
            // screen and hands the answer back through App.OnActivated, which is why this leaves a
            // handler behind instead of awaiting anything.
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            // The same three GContactsSync offers: the association, the extension people
            // reach for anyway, and "*" for a file saved under some other name entirely.
            picker.FileTypeFilter.Add(".cfg");
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add("*");

            App.FilePicked = async file =>
            {
                Apply(await CredentialsFileReader.LoadAsync(file));
            };

            try
            {
                picker.PickSingleFileAndContinue();
            }
            catch (Exception ex)
            {
                App.FilePicked = null;
                ShowError("Could not open the file picker: " + ex.Message);
            }
        }

        void Apply(CredentialsFileResult result)
        {
            if (result == null) { ShowError("Nothing was read."); return; }

            string where = result.Source.Length > 0 ? result.Source : "the chosen file";

            // An incomplete file still fills in whatever it did have - a wrong password with the
            // right address saves most of the typing.
            if (result.Email.Length > 0) EmailBox.Text = result.Email;
            if (result.Password.Length > 0) PasswordBox.Password = result.Password;

            if (result.Problem != null)
            {
                ShowError("Read " + where + ", but " + result.Problem + ".\n\n" +
                          "Expected two lines:\n" +
                          "login: you@gmail.com\n" +
                          "password: your16charapppw");
                return;
            }

            ResultText.Text = "Account loaded from " + where + " (" + result.Email + ").";
            ResultBanner.Visibility = Visibility.Visible;
        }

        async void OnForgetClick(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog(
                "Forget the saved account and everything this app remembers about previous syncs? " +
                "Events already on the phone are left alone, and the next sync will be a full one.",
                "forget account");
            dialog.Commands.Add(new UICommand("forget"));
            dialog.Commands.Add(new UICommand("cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;

            var chosen = await dialog.ShowAsync();
            if (chosen == null || chosen.Label != "forget") return;

            BackgroundSync.Unregister();
            CredentialStore.Delete();
            await SyncState.ClearAsync();
            AppSettings.LastDownload = null;
            AppSettings.LastAutoSync = null;
            AppSettings.LastAutoSyncError = null;

            EmailBox.Text = string.Empty;
            PasswordBox.Password = string.Empty;
            HideBanners();
            RefreshStatus();
        }

        // ================================================================
        // Helpers
        // ================================================================
        bool ReadAccount(out string email, out string password)
        {
            email = EmailBox.Text.Trim();
            // App Passwords are shown to the user in four groups of four; Google wants them
            // without the spaces.
            password = PasswordBox.Password.Replace(" ", "").Trim();

            if (email.Length == 0 || email.IndexOf('@') < 0)
            {
                ShowError("Enter the Gmail address of the account to sync.");
                return false;
            }
            if (password.Length < 16)
            {
                ShowError("Enter the 16-character Google App Password for this account - " +
                          "not the account's own password.");
                return false;
            }

            if (RememberBox.IsChecked == true) CredentialStore.Save(email, password);
            return true;
        }

        IProgress<string> ProgressReporter()
        {
            // SyncEngine reports from a background thread, so every update has to come back
            // through the dispatcher before it touches a control.
            return new Progress<string>(message =>
            {
                var ignored = Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal, () => ProgressText.Text = message);
            });
        }

        void SetBusy(bool busy)
        {
            DownloadButton.IsEnabled = !busy;
            EmailBox.IsEnabled = !busy;
            PasswordBox.IsEnabled = !busy;
            SettingsCmd.IsEnabled = !busy;
            ForgetCmd.IsEnabled = !busy;

            ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) { HideBanners(); ProgressText.Text = string.Empty; }
        }

        void Show(SyncOutcome outcome)
        {
            if (!outcome.Success) { ShowError(outcome.Error); return; }

            ResultText.Text = outcome.Message;
            ResultBanner.Visibility = Visibility.Visible;
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        void ShowError(string message)
        {
            ErrorText.Text = string.IsNullOrEmpty(message) ? "Unknown error." : message;
            ErrorBanner.Visibility = Visibility.Visible;
            ResultBanner.Visibility = Visibility.Collapsed;
        }

        void HideBanners()
        {
            ResultBanner.Visibility = Visibility.Collapsed;
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        void RefreshStatus()
        {
            LastDownloadText.Text = "last sync: " + (AppSettings.LastDownload ?? "never");

            if (!AppSettings.AutoSyncEnabled)
                AutoSyncText.Text = "automatic sync: off";
            else if (!BackgroundSync.IsRegistered)
                AutoSyncText.Text = "automatic sync: not scheduled yet";
            else
                AutoSyncText.Text = "automatic sync: every " +
                    AppSettings.AutoSyncIntervalMinutes + " min - last run " +
                    (AppSettings.LastAutoSync ?? "not yet");

            string error = AppSettings.LastAutoSyncError;
            if (string.IsNullOrEmpty(error))
            {
                AutoSyncErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                AutoSyncErrorText.Text = "last automatic run: " + error;
                AutoSyncErrorText.Visibility = Visibility.Visible;
            }
        }
    }
}
