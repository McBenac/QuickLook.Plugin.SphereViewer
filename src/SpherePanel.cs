using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLook.Plugin.SphereViewer;

/// <summary>
/// Hosts the bundled web viewer (viewer/index.html) in WebView2. The viewer files and the media
/// folder are exposed on two virtual hosts; the page reports back "ready" or "error:&lt;text&gt;".
/// </summary>
internal sealed class SpherePanel : UserControl, IDisposable
{
    private const string AppHost = "app.sphereviewer.example";
    private const string MediaHost = "media.sphereviewer.example";
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    // Horizon levelling from the motion sensor data (see TiltTrack). Off = files shown as recorded.
    private static readonly bool LevelHorizon = true;

    private static readonly string ViewerFolder =
        Path.Combine(Path.GetDirectoryName(typeof(SpherePanel).Assembly.Location), "viewer");

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timeout;
    private SphereMedia _media;
    private WebView2 _webView;
    private bool _finished;

    public SpherePanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 24));
        _timeout = new DispatcherTimer { Interval = LoadTimeout };
        _timeout.Tick += (_, _) => Fail("Timed out while loading the preview.");
    }

    /// <summary>Raised once: null when the panorama is shown, otherwise the error text.</summary>
    public event Action<string> Finished;

    /// <summary>Step-by-step trace for the test harness; silent inside QuickLook.</summary>
    internal static Action<string> Trace = _ => { };

    public void Load(SphereMedia media)
    {
        _media = media;

        if (!IsWebView2Available())
        {
            Fail("Microsoft Edge WebView2 Runtime is required to show 360° previews.");
            return;
        }

        _webView = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(SettingHelper.LocalDataPath, "SphereViewer_WebView2"),
                // Start videos with sound, like QuickLook's own video viewer.
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            },
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 24, 24, 24),
        };
        _webView.CoreWebView2InitializationCompleted += OnInitialized;
        _webView.NavigationCompleted += OnNavigationCompleted;
        Content = _webView;
        _timeout.Start();
        Trace("WebView2 runtime " + CoreWebView2Environment.GetAvailableBrowserVersionString());
        _ = _webView.EnsureCoreWebView2Async();
    }

    private async void OnInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        Trace($"WebView2 initialized: success={e.IsSuccess} {e.InitializationException?.Message}");
        if (_webView == null)
            return; // preview already closed

        if (!e.IsSuccess)
        {
            Fail("Could not start WebView2: " + e.InitializationException?.Message);
            return;
        }

        try
        {
            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationStarting += (_, args) =>
            {
                // The page never navigates; block anything else (e.g. a file dropped on the view).
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Host != AppHost)
                    args.Cancel = true;
            };
            core.WebMessageReceived += OnWebMessage;

            // Allow = served as if with "Access-Control-Allow-Origin: *", which WebGL textures need.
            core.SetVirtualHostNameToFolderMapping(AppHost, ViewerFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(MediaHost, Path.GetDirectoryName(_media.SourcePath),
                CoreWebView2HostResourceAccessKind.Allow);

            if (!LevelHorizon)
                Trace("horizon levelling off");
            if (LevelHorizon && _media.Projection == Projection.DualFisheye)
            {
                // Camera tilt from the file's motion sensor data, read off the UI thread.
                var path = _media.SourcePath;
                var isVideo = _media.IsVideo;
                var tilt = await System.Threading.Tasks.Task.Run(() =>
                    isVideo ? TiltTrack.ReadJson(path) : TiltTrack.ReadPhotoJson(path));
                Trace(tilt == null ? "no motion data" : $"motion data: {tilt.Length} chars");
                if (_webView == null)
                    return; // preview closed meanwhile
                if (tilt != null)
                    await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.sphereViewerTilt = {tilt};");
                if (_webView == null)
                    return;
            }

            _webView.Source = new Uri(BuildViewerUrl(_media));
            Trace("navigating to " + _webView.Source);
        }
        catch (Exception ex)
        {
            Fail("Could not open the preview: " + ex.Message);
        }
    }

    internal static string BuildViewerUrl(SphereMedia media)
    {
        var source = $"https://{MediaHost}/{Uri.EscapeDataString(Path.GetFileName(media.SourcePath))}";
        var projection = media.Projection == Projection.DualFisheye ? "dualfisheye" : "equirect";
        return $"https://{AppHost}/index.html?src={Uri.EscapeDataString(source)}" +
               $"&projection={projection}&video={(media.IsVideo ? 1 : 0)}" +
               $"&name={Uri.EscapeDataString(Path.GetFileName(media.OriginalPath))}";
    }

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Trace($"navigation completed: success={e.IsSuccess} status={e.WebErrorStatus}");
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            Fail("Could not load the viewer: " + e.WebErrorStatus);
    }

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var origin) || origin.Host != AppHost)
            return;

        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        Trace("message from viewer: " + message);
        if (message == "ready")
            Finish(null);
        else if (message.StartsWith("error:", StringComparison.Ordinal))
            Finish(message.Substring("error:".Length));
    }

    /// <summary>Shows the error in place of the viewer and ends loading.</summary>
    private void Fail(string error)
    {
        if (_finished)
            return;
        ShowMessage(error);
        Finish(error);
    }

    private void Finish(string error)
    {
        if (_finished)
            return;
        _finished = true;
        _timeout.Stop();

        var name = Path.GetFileName(_media?.OriginalPath);
        var detail = _media == null ? "" : $"{_media.Projection} video={_media.IsVideo} proxy={_media.IsProxy}";
        Log.Write(error == null
            ? $"OK    {name} {detail} {_clock.ElapsedMilliseconds} ms"
            : $"ERROR {name} {detail} {_clock.ElapsedMilliseconds} ms: {error}");

        Finished?.Invoke(error);
    }

    private void ShowMessage(string text)
    {
        // The WebView2 (if any) is disposed in Dispose(); it may still be raising the current event.
        Content = new TextBlock
        {
            Text = text,
            Foreground = Brushes.Gainsboro,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
    }

    private static bool IsWebView2Available()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    internal System.Threading.Tasks.Task CapturePngAsync(Stream output) =>
        _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);

    public void Dispose()
    {
        _finished = true; // no callbacks after the preview is closed
        _timeout.Stop();
        if (_webView != null)
        {
            _webView.CoreWebView2InitializationCompleted -= OnInitialized;
            _webView.NavigationCompleted -= OnNavigationCompleted;
            _webView.Dispose();
            _webView = null;
        }
    }
}
