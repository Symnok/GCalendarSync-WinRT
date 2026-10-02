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
// Startup is written defensively on purpose. There is no debugger on the phone, and a failure
// anywhere in here shows as nothing but a black screen, so anything thrown is painted on screen
// together with how far startup got.
using System;
using System.Text;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace GCalendarSync
{
    public sealed partial class App : Application
    {
        /// <summary>
        /// Where a picked file is delivered. WP8.1 suspends the app while the file picker is on
        /// screen, so the result arrives through OnActivated rather than as a return value, and the
        /// page that asked for it has to leave a handler behind.
        /// </summary>
        public static Action<StorageFile> FilePicked;

        static readonly StringBuilder Trace = new StringBuilder();

        Exception _startupFailure;

        public App()
        {
            // First, before anything that can throw: hooking this after InitializeComponent means
            // a failure parsing App.xaml bypasses the handler and the app dies silently.
            UnhandledException += OnUnhandledException;

            Note("App() " + BuildVersion);
            try
            {
                InitializeComponent();
                Note("InitializeComponent ok");
            }
            catch (Exception ex)
            {
                Note("InitializeComponent FAILED");
                _startupFailure = ex;
            }

            // On WP8.1 the hardware Back button closes the app unless a page handles it, so
            // backing out of Settings would exit instead of returning to the main page.
            Windows.Phone.UI.Input.HardwareButtons.BackPressed += OnBackPressed;
        }

        /// <summary>The deployed package version, so a screen can never be mistaken for an older build.</summary>
        public static string BuildVersion
        {
            get
            {
                try
                {
                    var v = Package.Current.Id.Version;
                    return v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision;
                }
                catch (Exception)
                {
                    return "?";
                }
            }
        }

        static void OnBackPressed(object sender, Windows.Phone.UI.Input.BackPressedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.CanGoBack)
            {
                e.Handled = true;
                frame.GoBack();
            }
            // Not handled at the root: the shell closes the app, which is what a user expects
            // from the first screen.
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Note("OnLaunched");
            try
            {
                if (_startupFailure != null)
                {
                    ShowFatal(_startupFailure, "App.xaml could not be loaded.");
                    return;
                }

                EnsureMainPage();
                Window.Current.Activate();
            }
            catch (Exception ex)
            {
                ShowFatal(ex, "Failed during OnLaunched.");
            }
        }

        static void EnsureMainPage()
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                // Without this, a page whose constructor throws fails silently: Navigate returns,
                // the exception is swallowed, and Content is left null.
                frame.NavigationFailed += (sender, args) =>
                {
                    args.Handled = true;
                    ShowFatal(args.Exception, "Could not construct " +
                        (args.SourcePageType == null ? "(unknown page)" : args.SourcePageType.FullName) + ".");
                };
                Window.Current.Content = frame;
            }

            if (frame.Content == null) frame.Navigate(typeof(MainPage));
            Note("navigated");
        }

        /// <summary>
        /// How the file picker's answer comes back. The app was suspended while the picker was up,
        /// so the page that started it is normally still there with its handler in place; if the
        /// phone terminated us instead, the handler is gone and the pick is simply dropped.
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            Note("OnActivated " + args.Kind);
            try
            {
                EnsureMainPage();
                Window.Current.Activate();

                var continuation = args as FileOpenPickerContinuationEventArgs;
                if (continuation == null) return;

                var handler = FilePicked;
                FilePicked = null;
                if (handler == null) return;

                StorageFile file = null;
                if (continuation.Files != null && continuation.Files.Count > 0)
                    file = continuation.Files[0];
                handler(file);
            }
            catch (Exception ex)
            {
                ShowFatal(ex, "Failed during OnActivated.");
            }
        }

        void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            ShowFatal(e.Exception, e.Message);
        }

        static void Note(string stage)
        {
            Trace.AppendLine(stage);
        }

        /// <summary>
        /// Replaces whatever is on screen with the failure and how far startup got. On a phone
        /// with no debugger attached this is the only diagnostic channel there is.
        /// </summary>
        static void ShowFatal(Exception exception, string message)
        {
            try
            {
                var text = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Margin = new Thickness(14),
                    Text = "GCalendarSync " + BuildVersion + " could not start.\n\n"
                         + "stage reached:\n" + Trace + "\n"
                         + (message ?? string.Empty) + "\n\n"
                         + (exception == null ? "(no exception object)" : exception.ToString())
                };

                Window.Current.Content = new ScrollViewer { Content = text };
                Window.Current.Activate();
            }
            catch (Exception)
            {
                // If even this fails there is nothing left to try.
            }
        }
    }
}
