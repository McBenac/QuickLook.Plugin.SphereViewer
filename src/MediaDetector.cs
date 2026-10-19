using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickLook.Plugin.SphereViewer;

internal enum Projection
{
    Equirectangular,
    DualFisheye,
}

internal sealed class SphereMedia
{
    public SphereMedia(string originalPath, string sourcePath, Projection projection, bool isVideo)
    {
        OriginalPath = originalPath;
        SourcePath = sourcePath;
        Projection = projection;
        IsVideo = isVideo;
    }

    /// <summary>The file the user pressed Space on.</summary>
    public string OriginalPath { get; }

    /// <summary>The file actually rendered (the .lrv proxy for an .insv).</summary>
    public string SourcePath { get; }

    public Projection Projection { get; }

    public bool IsVideo { get; }

    public bool IsProxy => !string.Equals(OriginalPath, SourcePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Decides from the file name and a small header read whether a file is 360° media this plugin
/// should show. Must stay cheap: QuickLook calls it for every file the user previews.
/// </summary>
internal static class MediaDetector
{
    private const int XmpScanBytes = 128 * 1024;

    // VID_20261003_170641_00_625.insv -> LRV_20261003_170641_01_625.lrv
    private static readonly Regex InsvName = new(@"^VID_(\d{8}_\d{6})_\d{2}_(\d+)\.insv$", RegexOptions.IgnoreCase);

    private static readonly Regex LrvName = new(@"^LRV_\d{8}_\d{6}_\d{2}_\d+\.lrv$", RegexOptions.IgnoreCase);

    public static SphereMedia Detect(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".insp":
                // Insta360 photo: a JPEG holding both fisheye images side by side.
                return IsJpeg(ReadHead(path, 3))
                    ? new SphereMedia(path, path, Projection.DualFisheye, false)
                    : null;

            case ".lrv":
                // Low-resolution H.264 copy of an Insta360 video, same dual-fisheye layout. Other
                // cameras (e.g. GoPro) also write .lrv files, so require the Insta360 name.
                return LrvName.IsMatch(Path.GetFileName(path))
                    ? new SphereMedia(path, path, Projection.DualFisheye, true)
                    : null;

            case ".insv":
                // Two HEVC tracks in one file cannot play in a web view; show its .lrv copy instead.
                var proxy = FindLrvProxy(path);
                return proxy == null ? null : new SphereMedia(path, proxy, Projection.DualFisheye, true);

            case ".jpg":
            case ".jpeg":
                return IsEquirectangularJpeg(ReadHead(path, XmpScanBytes))
                    ? new SphereMedia(path, path, Projection.Equirectangular, false)
                    : null;

            default:
                return null;
        }
    }

    internal static string FindLrvProxy(string insvPath)
    {
        var match = InsvName.Match(Path.GetFileName(insvPath));
        if (!match.Success)
            return null;

        var lrv = Path.Combine(Path.GetDirectoryName(insvPath),
            $"LRV_{match.Groups[1].Value}_01_{match.Groups[2].Value}.lrv");
        return File.Exists(lrv) ? lrv : null;
    }

    private static bool IsJpeg(byte[] head) =>
        head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;

    /// <summary>
    /// A JPEG whose XMP packet has GPano:ProjectionType = equirectangular, in either the attribute
    /// form (ProjectionType="equirectangular") or the element form (&lt;GPano:ProjectionType&gt;equirectangular&lt;/…&gt;).
    /// Cameras write XMP in the first APP1 segments, well inside the scanned head.
    /// </summary>
    internal static bool IsEquirectangularJpeg(byte[] head)
    {
        if (!IsJpeg(head))
            return false;

        var text = Encoding.ASCII.GetString(head);
        var index = text.IndexOf("ProjectionType", StringComparison.Ordinal);
        while (index >= 0)
        {
            var window = text.Substring(index, Math.Min(48, text.Length - index));
            if (window.IndexOf("equirectangular", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            index = text.IndexOf("ProjectionType", index + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static byte[] ReadHead(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(count, stream.Length)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
            return buffer;
        }
        catch (IOException)
        {
            return new byte[0];
        }
        catch (UnauthorizedAccessException)
        {
            return new byte[0];
        }
    }
}
