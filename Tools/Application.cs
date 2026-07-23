//BEGIN_FILE HFT/Tools/Application.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tools
{
    public static class Application
    {
        public sealed class ExitAction
        {
            public string Name { get; }
            public Action Action { get; }
            public int Priority { get; }

            public ExitAction(string name, int priority, Action action)
            {
                Name = name;
                Action = action;
                Priority = priority;
            }
        }

        public static ConcurrentQueue<ExitAction> Actions { get; } = new ConcurrentQueue<ExitAction>();

        private static int s_exiting = 0;
        private static readonly ManualResetEventSlim s_exited = new ManualResetEventSlim(false);

        public static bool IsExiting => Volatile.Read(ref s_exiting) == 1;

        // Native Windows console control handler delegate
        private delegate bool ConsoleCtrlDelegate(int ctrlType);

        // Windows-specific Import
        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        // Keep a reference to the delegate to prevent GC collection
        private static readonly ConsoleCtrlDelegate _ctrlHandler = ConsoleCtrlHandler;

        static Application()
        {
            // Windows-specific: Handles X button, logoff, etc.
            // We guard this so it does not crash on Linux.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SetConsoleCtrlHandler(_ctrlHandler, true);
            }

            // Cross-Platform: Handles Ctrl+C
            Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
            {
                Console.WriteLine("Terminal Signal (Ctrl+C) Captured. Shutting down...");
                // On Linux, this captures SIGINT
                OnExit(null, null);

                // Allow the process to terminate naturally after cleanup
                e.Cancel = false;
            };

            // Cross-Platform: Handles AppDomain unload / normal exit / SIGTERM (Linux)
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                // Unhandled exceptions and standard framework exits trigger this
                OnExit(null, null);
            };
        }

        /// <summary>Adds an action that will run once on application exit.</summary>
        public static void AddExitAction(string name, Action action)
        {
            AddExitAction(name, 0, action);
        }

        /// <summary>Adds an action that will run once on application exit. Order by priority (higher values occur earlier)</summary>
        public static void AddExitAction(string name, int priority, Action action)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or whitespace.", nameof(name));

            if (action == null)
                throw new ArgumentNullException(nameof(action));

            Actions.Enqueue(new ExitAction(name, priority, action));
        }

        /// <summary>Runs all exit actions exactly once.</summary>
        public static void OnExit(object? sender, EventArgs? e)
        {
            // Ensure we only run cleanup once. A later caller (e.g. ProcessExit firing after the main
            // loop saw IsExiting and returned from Main) must BLOCK until the chain completes —
            // returning immediately lets CLR shutdown kill the first caller's thread mid-action.
            if (Interlocked.Exchange(ref s_exiting, 1) == 1)
            {
                s_exited.Wait();
                return;
            }

            Console.WriteLine("Application::OnExit() - Running cleanup actions...");

            // Snapshot the queue to an array so we can sort by priority.
            ExitAction[] snapshot = Actions.ToArray();
            ExitAction[] ordered = snapshot.OrderByDescending(exitAction => exitAction.Priority).ToArray();

            foreach (ExitAction act in ordered)
            {
                try
                {
                    Console.WriteLine($"Application::Executing exit action '{act.Name}' with priority {act.Priority}...");
                    act.Action();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Exit action '{act.Name}' failed: {ex}");
                }
            }

            s_exited.Set();
        }

        // Native Windows handler (X button, shutdown, logoff)
        // This method is only invoked by the Windows API callback.
        private static bool ConsoleCtrlHandler(int ctrlType)
        {
            Console.WriteLine("Console Ctrl Handler Captured. Shutting down...");
            OnExit(null, null);
            return false; // allow normal shutdown
        }
    }
}
//END_FILE HFT/Tools/Application.cs