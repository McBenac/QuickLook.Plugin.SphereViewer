using QuickLook.Common.Plugin;
using System.IO;
using System.Windows;

namespace QuickLook.Plugin.SphereViewer;

public class Plugin : IViewer
{
    private SphereMedia _media;
    private SpherePanel _panel;

    // Above ImageViewer (0) and VideoViewer (-3). CanHandle is narrow, so ordinary photos and
    // videos still go to those viewers.
    public int Priority => 10;

    public void Init()
    {
    }

    public bool CanHandle(string path) => !Directory.Exists(path) && MediaDetector.Detect(path) != null;

    public void Prepare(string path, ContextObject context)
    {
        _media = MediaDetector.Detect(path);
        context.Title = _media is { IsProxy: true }
            ? $"{Path.GetFileName(path)} (preview: {Path.GetFileName(_media.SourcePath)})"
            : Path.GetFileName(path);
        context.Theme = Themes.Dark;

        var area = SystemParameters.WorkArea;
        context.PreferredSize = new Size(System.Math.Min(1280, area.Width * 0.8), System.Math.Min(800, area.Height * 0.8));
    }

    public void View(string path, ContextObject context)
    {
        if (_media == null) // file changed between CanHandle and Prepare
        {
            context.IsBusy = false;
            return;
        }

        _panel = new SpherePanel();
        _panel.Finished += _ => context.IsBusy = false;
        context.ViewerContent = _panel;
        _panel.Load(_media);
    }

    public void Cleanup()
    {
        _panel?.Dispose();
        _panel = null;
        _media = null;
    }
}
