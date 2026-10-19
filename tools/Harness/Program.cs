using QuickLook.Plugin.SphereViewer;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "detect")
            return Detect(args);
        if (args.Length == 2 && args[0] == "tilt")
        {
            var photo = args[1].EndsWith(".insp", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine((photo ? TiltTrack.ReadPhotoJson(args[1]) : TiltTrack.ReadJson(args[1])) ?? "null");
            return 0;
        }
        if (args.Length >= 3 && args[0] == "render")
            return Render(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 30);

        Console.WriteLine("Usage: SphereViewer.Harness detect <file>...");
        Console.WriteLine("       SphereViewer.Harness render <file> <out.png> [timeoutSeconds]");
        Console.WriteLine("       SphereViewer.Harness tilt <file.lrv|file.insv|file.insp>");
        return 2;
    }

    private static int Detect(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            var media = MediaDetector.Detect(args[i]);
            var verdict = media == null
                ? "-"
                : media.Projection + (media.IsVideo ? " video" : " photo") +
                  (media.IsProxy ? " via " + Path.GetFileName(media.SourcePath) : "");
            Console.WriteLine($"{verdict}\t{Path.GetFileName(args[i])}");
        }
        return 0;
    }

    private static int Render(string file, string png, int timeoutSeconds)
    {
        var media = MediaDetector.Detect(file);
        if (media == null)
        {
            Console.WriteLine("NOT HANDLED " + file);
            return 3;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        SpherePanel.Trace = line => Console.WriteLine($"[{clock.ElapsedMilliseconds,6} ms] {line}");
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var panel = new SpherePanel();
        var window = new Window { Width = 1280, Height = 800, Title = "SphereViewer harness", Content = panel };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        timeout.Tick += (_, _) =>
        {
            Console.WriteLine("TIMEOUT");
            app.Shutdown();
        };

        panel.Finished += async error =>
        {
            timeout.Stop();
            if (error != null)
            {
                Console.WriteLine("ERROR " + error);
                app.Shutdown();
                return;
            }

            await Task.Delay(1500); // let the first frames render
            using (var output = File.Create(png))
                await panel.CapturePngAsync(output);
            Console.WriteLine("OK " + png);
            exitCode = 0;
            app.Shutdown();
        };

        window.Loaded += (_, _) =>
        {
            SpherePanel.Trace("window loaded");
            timeout.Start();
            panel.Load(media);
        };
        app.Run(window);
        return exitCode;
    }
}
