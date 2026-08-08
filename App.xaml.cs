using Microsoft.UI.Xaml;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace pdf_studio
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = "PDFStudio.SingleInstance";
        private const string PipeName = "PDFStudio.FileActivation";

        private static Mutex? _singleInstanceMutex;

        private MainWindow? _window;
        private CancellationTokenSource? _pipeCts;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var filePath = GetFileArgument();

            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running: hand it the file (if
                // any) so it opens in a new tab, then shut this process down.
                ForwardToExistingInstance(filePath);
                Environment.Exit(0);
                return;
            }

            _window = new MainWindow();
            _window.Activate();
            StartPipeServer();

            if (filePath != null)
            {
                _window.OpenPdfFromPath(filePath);
            }
        }

        /// <summary>
        /// Unpackaged WinUI apps receive no launch arguments through
        /// LaunchActivatedEventArgs; the file path comes via the command line.
        /// </summary>
        private static string? GetFileArgument()
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            for (var i = 1; i < cmdArgs.Length; i++)
            {
                var candidate = cmdArgs[i].Trim().Trim('"');
                if (candidate.Length > 0 && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static void ForwardToExistingInstance(string? filePath)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.Write(filePath ?? string.Empty);
                writer.Flush();
            }
            catch
            {
                // Existing instance not reachable — nothing more we can do.
            }
        }

        private void StartPipeServer()
        {
            _pipeCts = new CancellationTokenSource();
            var token = _pipeCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Message,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(server);
                        var message = await reader.ReadToEndAsync(token);

                        var window = _window;
                        window?.DispatcherQueue.TryEnqueue(() =>
                        {
                            window.BringToForeground();
                            if (!string.IsNullOrWhiteSpace(message) && File.Exists(message))
                            {
                                window.OpenPdfFromPath(message);
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Keep the server alive across transient pipe errors.
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                }
            }, token);
        }
    }
}
