#region License statement
/* SnakeTail is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, version 3 of the License.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;

namespace SnakeTail
{
    static class Program
    {
        static volatile bool applicationCrashed = false;
        /// <summary>GitHub Releases API (used to check for updates), matching the tags published by the publish workflow.</summary>
        public static readonly string GitHubReleasesApiUrl = "https://api.github.com/repos/sunnybug/snaketail-net/releases/latest";

        const string SingleInstanceMutexName = "Local\\SnakeTail_SingleInstance";
        const string SingleInstancePipeName = "SnakeTail_SingleInstance_Pipe";

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Only try to activate the first process and pass arguments when a log file argument is given; with no arguments always start a new process (new window)
                    var fileArgs = GetCommandLineFileArgs();
                    if (fileArgs.Length > 0 && TrySendArgsToFirstInstance(fileArgs))
                        return;
                    // No arguments, or the first process isn't ready / the pipe is unavailable: this process still starts normally
                }

                try
                {
                    ApplicationConfiguration.Initialize();

                    AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

                    Application.EnableVisualStyles();
                    Application.ThreadException += new System.Threading.ThreadExceptionEventHandler(Application_ThreadException);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.Automatic);
                    Application.SetCompatibleTextRenderingDefault(false);

                    var mainForm = new MainForm();
                    Application.Run(mainForm);
                }
                catch (Exception ex)
                {
                    AppLog.AppendCrash(ex, "SnakeTail startup");
                    // Bind an owner during startup too, so the dialog isn't hidden behind other windows
                    MessageBox.Show(DialogOwner.Resolve(), ex.ToString(), "SnakeTail Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>Extracts the file/session paths to open from the command-line arguments (skipping the exe itself).</summary>
        static string[] GetCommandLineFileArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args == null || args.Length <= 1)
                return Array.Empty<string>();
            var list = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(args[i]))
                    list.Add(args[i].Trim());
            }
            return list.ToArray();
        }

        /// <summary>Second instance: sends the arguments to the first process through a named pipe; returns true on success. Only used by the caller when a log file is passed.</summary>
        static bool TrySendArgsToFirstInstance(string[] paths)
        {
            if (paths == null)
                paths = Array.Empty<string>();
            for (int retry = 0; retry < 30; retry++)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out))
                    {
                        client.Connect(100);
                        using (var bw = new BinaryWriter(client, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            bw.Write(paths.Length);
                            foreach (string p in paths)
                                bw.Write(p ?? "");
                        }
                    }
                    return true;
                }
                catch (TimeoutException) { }
                catch (IOException) { }
                Thread.Sleep(100);
            }
            return false;
        }

        /// <summary>Called by MainForm on Shown to start the single-instance pipe service (background thread).</summary>
        public static void StartSingleInstancePipeServer()
        {
            var th = new Thread(RunSingleInstancePipeServer)
            {
                IsBackground = true,
                Name = "SnakeTail.SingleInstancePipe"
            };
            th.Start();
        }

        static void RunSingleInstancePipeServer()
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(SingleInstancePipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        server.WaitForConnection();
                        var paths = new List<string>();
                        using (var br = new BinaryReader(server, System.Text.Encoding.UTF8, leaveOpen: true))
                        {
                            int count = br.ReadInt32();
                            for (int i = 0; i < count; i++)
                                paths.Add(br.ReadString());
                        }
                        server.Disconnect();
                        if (paths.Count > 0 && MainForm.Instance != null && !MainForm.Instance.IsDisposed)
                            MainForm.Instance.BringToFrontAndOpenOrActivateTab(paths.ToArray());
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    // Client disconnected, etc.
                }
                catch (Exception)
                {
                    // Ignore a single error and keep accepting the next connection
                }
            }
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (applicationCrashed)
                return;
            applicationCrashed = true;

            if (e.ExceptionObject is Exception)
                SendCrashReport(e.ExceptionObject as Exception);
            else
                SendCrashReport(new Exception(string.Format("Unknown Exception - {0}", e.ExceptionObject)));

            applicationCrashed = false;
        }

        static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            if (applicationCrashed)
                return;
            applicationCrashed = true;

            SendCrashReport(e.Exception);

            applicationCrashed = false;
        }

        static void SendCrashReport(Exception ex)
        {
            try
            {
                if (ex == null)
                    ex = new Exception("Unknown exception (null reference)");

                ThreadExceptionDialogEx dlg = new ThreadExceptionDialogEx(ex);
                if (MainForm.Instance != null && !MainForm.Instance.IsDisposed)
                    dlg.ShowDialog(MainForm.Instance);
                else
                    dlg.ShowDialog();
            }
            catch (Exception dialogEx)
            {
                AppLog.AppendCrash(ex, "--- Dialog failed ---" + Environment.NewLine + dialogEx);
                string crashPath = AppLog.GetCrashLogFilePath();
                MessageBox.Show(
                    DialogOwner.Resolve(),
                    "Could not show the error report dialog." + Environment.NewLine + Environment.NewLine +
                    "Original exception: " + (ex?.Message ?? "null") + Environment.NewLine + Environment.NewLine +
                    "Details written to: " + crashPath,
                    Application.ProductName + " - Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
