#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Windows.Win32;
using Windows.Win32.Foundation;

using Bloxstrap.Enums;
using Bloxstrap.UI.Elements.Dialogs;

namespace Bloxstrap
{
    /// <summary>
    /// Top-level dispatcher for all launch flows (installer, uninstaller, settings, Roblox, watcher services, etc).
    /// </summary>
    public static class LaunchHandler
    {
        private const string LogIdent = "LaunchHandler";

        // -------------------------
        // Public entry points
        // -------------------------

        /// <summary>
        /// Routes to the next action after a modal/menu closes.
        /// </summary>
        public static void ProcessNextAction(NextAction action, bool isUnfinishedInstall = false)
        {
            const string ident = $"{LogIdent}::ProcessNextAction";

            switch (action)
            {
                case NextAction.LaunchSettings:
                    App.Logger.WriteLine(ident, "Opening settings");
                    LaunchSettings();
                    break;

                case NextAction.LaunchRoblox:
                    App.Logger.WriteLine(ident, "Opening Roblox");
                    LaunchRoblox(LaunchMode.Player);
                    break;

                case NextAction.LaunchRobloxStudio:
                    App.Logger.WriteLine(ident, "Opening Roblox Studio");
                    LaunchRoblox(LaunchMode.Studio);
                    break;

                default:
                    App.Logger.WriteLine(ident, "Closing");
                    App.Terminate(isUnfinishedInstall ? ErrorCode.ERROR_INSTALL_USEREXIT : ErrorCode.ERROR_SUCCESS);
                    break;
            }
        }

        /// <summary>
        /// Interprets initial CLI flags and launches the appropriate flow.
        /// </summary>
        public static void ProcessLaunchArgs()
        {
            const string ident = $"{LogIdent}::ProcessLaunchArgs";

            // Deliberate priority order:
            if (App.LaunchSettings.UninstallFlag.Active)
            {
                App.Logger.WriteLine(ident, "Opening uninstaller");
                LaunchUninstaller();
            }
            else if (App.LaunchSettings.MenuFlag.Active)
            {
                App.Logger.WriteLine(ident, "Opening settings");
                LaunchSettings();
            }
            else if (App.LaunchSettings.WatcherFlag.Active)
            {
                App.Logger.WriteLine(ident, "Opening watcher");
                LaunchWatcher();
            }
            else if (App.LaunchSettings.MultiInstanceWatcherFlag.Active)
            {
                App.Logger.WriteLine(ident, "Opening multi-instance watcher");
                LaunchMultiInstanceWatcher();
            }
            else if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(ident, "Opening background updater");
                LaunchBackgroundUpdater();
            }
            else if (App.LaunchSettings.RobloxLaunchMode != LaunchMode.None)
            {
                App.Logger.WriteLine(ident, $"Opening bootstrapper ({App.LaunchSettings.RobloxLaunchMode})");
                LaunchRoblox(App.LaunchSettings.RobloxLaunchMode);
            }
            else if (!App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(ident, "Opening menu");
                LaunchMenu();
            }
            else
            {
                App.Logger.WriteLine(ident, "Closing - quiet flag active");
                App.Terminate();
            }
        }

        /// <summary>
        /// Installer entry.
        /// </summary>
        public static void LaunchInstaller()
        {
            using var interlock = new InterProcessLock("Installer");

            if (!interlock.IsAcquired)
            {
                Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Installer, MessageBoxImage.Stop);
                App.Terminate();
                return;
            }

            if (App.LaunchSettings.UninstallFlag.Active)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_FirstRunUninstall, MessageBoxImage.Error);
                App.Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
                return;
            }

            if (App.LaunchSettings.QuietFlag.Active)
            {
                var installer = new Installer();

                if (!installer.CheckInstallLocation())
                {
                    App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                    return;
                }

                installer.DoInstall();

                interlock.Dispose();
                ProcessLaunchArgs();
                return;
            }

#if QA_BUILD
            Frontend.ShowMessageBox(
                "You are about to install a QA build of Bloxstrap. The red window border indicates that this is a QA build.\n\n" +
                "QA builds are handled completely separately from your standard installation, like a virtual environment.",
                MessageBoxImage.Information);
#endif

            new LanguageSelectorDialog().ShowDialog();

            var ui = new UI.Elements.Installer.MainWindow();
            ui.ShowDialog();

            interlock.Dispose();
            ProcessNextAction(ui.CloseAction, !ui.Finished);
        }

        /// <summary>
        /// Uninstaller entry with optional data retention.
        /// </summary>
        public static void LaunchUninstaller()
        {
            using var interlock = new InterProcessLock("Uninstaller");

            if (!interlock.IsAcquired)
            {
                Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Uninstaller, MessageBoxImage.Stop);
                App.Terminate();
                return;
            }

            bool confirmed;
            bool keepData = true;

            if (App.LaunchSettings.QuietFlag.Active)
            {
                confirmed = true;
            }
            else
            {
                var dialog = new UninstallerDialog();
                dialog.ShowDialog();
                confirmed = dialog.Confirmed;
                keepData = dialog.KeepData;
            }

            if (!confirmed)
            {
                App.Terminate();
                return;
            }

            Installer.DoUninstall(keepData);
            Frontend.ShowMessageBox(Strings.Bootstrapper_SuccessfullyUninstalled, MessageBoxImage.Information);
            App.Terminate();
        }

        /// <summary>
        /// Opens the Settings window (single-instance).
        /// </summary>
        public static void LaunchSettings()
        {
            const string ident = $"{LogIdent}::LaunchSettings";

            using var interlock = new InterProcessLock("Settings");

            if (interlock.IsAcquired)
            {
                bool showAlreadyRunningWarning = Process.GetProcessesByName(App.ProjectName).Length > 1;
                var window = new UI.Elements.Settings.MainWindow(showAlreadyRunningWarning);

                // Block to keep IPL in scope:
                window.ShowDialog();
                return;
            }

            App.Logger.WriteLine(ident, "Found an already existing menu window");

            var process = Utilities.GetProcessesSafe()
                .FirstOrDefault(x => x.MainWindowTitle == Strings.Menu_Title);

            if (process is not null)
                PInvoke.SetForegroundWindow((HWND)process.MainWindowHandle);

            App.Terminate();
        }

        /// <summary>
        /// Opens the initial menu dialog and dispatches the next action.
        /// </summary>
        public static void LaunchMenu()
        {
            var dialog = new LaunchMenuDialog();
            dialog.ShowDialog();
            ProcessNextAction(dialog.CloseAction);
        }

        /// <summary>
        /// Launches Roblox in the requested mode via the Bootstrapper. Shows UX unless Quiet.
        /// </summary>
        public static void LaunchRoblox(LaunchMode launchMode)
        {
            const string ident = $"{LogIdent}::LaunchRoblox";

            if (launchMode == LaunchMode.None)
                throw new InvalidOperationException("No Roblox launch mode set");

            // WMF check
            if (!File.Exists(Path.Combine(Paths.System, "mfplat.dll")))
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_WMFNotFound, MessageBoxImage.Error);

                if (!App.LaunchSettings.QuietFlag.Active)
                    Utilities.ShellExecute("https://support.microsoft.com/en-us/topic/media-feature-pack-list-for-windows-n-editions-c1c6fffa-d052-8338-7a79-a4bb980a700a");

                App.Terminate(ErrorCode.ERROR_FILE_NOT_FOUND);
                return;
            }

            // Optional confirm if another instance detected and multi-instance not enabled
            if (App.Settings.Prop.ConfirmLaunches &&
                Mutex.TryOpenExisting("ROBLOX_singletonMutex", out _) &&
                !App.Settings.Prop.MultiInstanceLaunching)
            {
                // Note: singleton mutex can linger briefly post-close.
                var result = Frontend.ShowMessageBox(Strings.Bootstrapper_ConfirmLaunch, MessageBoxImage.Warning, MessageBoxButton.YesNo);
                if (result != MessageBoxResult.Yes)
                {
                    App.Terminate();
                    return;
                }
            }

            // Initialize bootstrapper
            App.Logger.WriteLine(ident, "Initializing bootstrapper");
            App.Bootstrapper = new Bootstrapper(launchMode);

            IBootstrapperDialog? dialog = null;

            if (!App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(ident, "Initializing bootstrapper dialog");
                dialog = App.Settings.Prop.BootstrapperStyle.GetNew();
                App.Bootstrapper.Dialog = dialog;
                dialog.Bootstrapper = App.Bootstrapper;
            }

            // Run bootstrapper and handle completion/faults centrally
            RunTaskWithLogging(App.Bootstrapper.Run, "Bootstrapper", onFinally: App.Terminate);

            // If we created a dialog, this blocks until closed:
            dialog?.ShowBootstrapper();

            App.Logger.WriteLine(ident, "Exiting");
        }

        /// <summary>
        /// Starts the activity watcher service (Discord RPC, presence updates, server info, etc.).
        /// </summary>
        public static void LaunchWatcher()
        {
            const string ident = $"{LogIdent}::LaunchWatcher";

            var watcher = new Watcher();

            RunTaskWithLogging(
                async () => await watcher.Run().ConfigureAwait(false),
                "Watcher",
                onFault: ex => App.Logger.WriteLine(ident, $"Watcher faulted: {ex}"),
                onFinally: () =>
                {
                    watcher.Dispose();
                    App.Terminate();
                });
        }

        /// <summary>
        /// Starts the multi-instance watcher.
        /// </summary>
        public static void LaunchMultiInstanceWatcher()
        {
            const string ident = $"{LogIdent}::LaunchMultiInstanceWatcher";
            App.Logger.WriteLine(ident, "Starting multi-instance watcher");

            RunTaskWithLogging(
                async () => await MultiInstanceWatcher.Run().ConfigureAwait(false),
                "MultiInstanceWatcher",
                onFinally: App.Terminate);
        }

        /// <summary>
        /// Background updater flow. Runs silently, abortable via named event.
        /// </summary>
        public static void LaunchBackgroundUpdater()
        {
            const string ident = $"{LogIdent}::LaunchBackgroundUpdater";

            // Ensure quiet background semantics
            App.LaunchSettings.QuietFlag.Active = true;
            App.LaunchSettings.NoLaunchFlag.Active = true;

            App.Logger.WriteLine(ident, "Initializing bootstrapper");
            App.Bootstrapper = new Bootstrapper(LaunchMode.Player)
            {
                MutexName = "Bloxstrap-BackgroundUpdater",
                QuitIfMutexExists = true
            };

            using var cts = new CancellationTokenSource();

            // Event waiter that cancels the bootstrapper
            _ = Task.Run(() =>
            {
                App.Logger.WriteLine(ident, "Started event waiter");
                using var handle = new EventWaitHandle(false, EventResetMode.AutoReset, "Bloxstrap-BackgroundUpdaterKillEvent");
                handle.WaitOne();
                App.Logger.WriteLine(ident, "Received close event, cancelling bootstrapper");
                App.Bootstrapper.Cancel();
            }, cts.Token);

            RunTaskWithLogging(
                App.Bootstrapper.Run,
                "BackgroundUpdater",
                onFinally: () =>
                {
                    cts.Cancel(); // stop event waiter
                    App.Terminate();
                });

            App.Logger.WriteLine(ident, "Exiting");
        }

        // -------------------------
        // Helpers
        // -------------------------

        /// <summary>
        /// Runs a task with standardized logging and fault handling.
        /// </summary>
        private static void RunTaskWithLogging(
            Func<Task> taskFactory,
            string taskName,
            Action<Exception>? onFault = null,
            Action? onFinally = null)
        {
            Task.Run(async () =>
            {
                try
                {
                    await taskFactory().ConfigureAwait(false);
                    App.Logger.WriteLine(LogIdent, $"{taskName} task has finished");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LogIdent, $"An exception occurred when running {taskName}: {ex}");
                    App.FinalizeExceptionHandling(ex);
                    onFault?.Invoke(ex);
                }
                finally
                {
                    onFinally?.Invoke();
                }
            });
        }
    }
}
