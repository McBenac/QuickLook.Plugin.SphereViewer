using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuickLook.Plugin.SphereViewer;

/// <summary>
/// Reads the camera's motion sensor data from the Insta360 trailer at the end of an .insv/.lrv file
/// and turns it into the direction of "up" in the camera frame over time, so the viewer can keep the
/// horizon level when the camera was held tilted.
/// </summary>
/// <remarks>
/// Trailer layout (little-endian): records <c>[data][id:u16][len:u32]</c>, the last one a directory of
/// 31 slots <c>[dirId:u16][size:u32][offset:u32]</c> (dirId = record id &gt;&gt; 8, offset from trailer start);
/// footer = last 78 bytes: last record header, 32 zero bytes, trailer length u32, version u32, magic.
/// Record 0x300 = IMU, 20 bytes per sample: time u64 (µs), then six u16 values (v - 0x8000) / 1000:
/// accelerometer (g) and gyroscope (rad/s). Record 0x400 = one entry per exposed frame: time u64 (µs) +
/// exposure f64. The recording keeps the last exposures, so the last exposure is the last video frame.
/// </remarks>
internal static class TiltTrack
{
    private const int FooterLength = 78;
    private const string Magic = "8db42d694ccc418790edff439fe026bf";
    private const double SmoothingSeconds = 0.5; // accelerometer weight in the complementary filter
    private const double OutputIntervalSeconds = 1 / 30.0;
    private const double PhotoExposureSeconds = 0.85; // photo sensor data starts this long before the exposure
    private const long MaxRecordBytes = 64 << 20; // IMU data for 4 minutes is ~5 MB

    /// <summary>
    /// JSON <c>{"tEnd":[...],"up":[x,y,z,...]}</c>: tEnd = seconds relative to the last video frame
    /// (≈ 0 at the end; the sensor data runs slightly past it), up = unit vector in the viewer's camera frame (x right, y up, z front lens). Null when the
    /// file has no motion data.
    /// </summary>
    public static string ReadJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var imu = ReadRecord(stream, 0x03);
            var exposures = ReadRecord(stream, 0x04);
            if (imu == null || exposures == null || imu.Length < 20 * 100 || exposures.Length < 16)
                return null;

            var lastFrameMicros = (double)BitConverter.ToUInt64(exposures, exposures.Length - 16);
            return Filter(imu, lastFrameMicros);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is OverflowException || ex is OutOfMemoryException)
        {
            return null; // a damaged trailer must never stop the preview itself
        }
    }

    /// <summary>
    /// JSON <c>{"up":[x,y,z]}</c> for an .insp photo: the camera's "up" in the viewer's camera frame at the
    /// moment of exposure. Null without data. Photo trailers have no directory; the records are walked
    /// backwards from the footer. Most photos keep several seconds of sensor data that start about
    /// <see cref="PhotoExposureSeconds"/> before the exposure, often while the camera is still moving;
    /// a few keep only a short still window, which is averaged.
    /// </summary>
    public static string ReadPhotoJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var imu = ReadPhotoRecord(stream, 0x300);
            if (imu == null || imu.Length < 20)
                return null;

            var count = imu.Length / 20;
            var first = (double)BitConverter.ToUInt64(imu, 0);
            var span = ((double)BitConverter.ToUInt64(imu, (count - 1) * 20) - first) / 1e6; // negative if damaged
            double x = 0, y = 0, z = 0;
            if (span > PhotoExposureSeconds)
            {
                var exposure = first + PhotoExposureSeconds * 1e6;
                Track(imu, (t, ux, uy, uz) =>
                {
                    x = ux;
                    y = uy;
                    z = uz;
                    return t < exposure;
                });
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    Accelerometer(imu, i * 20, out var ax, out var ay, out var az);
                    x += ax;
                    y += ay;
                    z += az;
                }
            }
            var norm = Math.Sqrt(x * x + y * y + z * z);
            if (!(norm > 1e-6) || double.IsInfinity(norm))
                return null;
            return string.Format(CultureInfo.InvariantCulture, "{{\"up\":[{0:0.####},{1:0.####},{2:0.####}]}}",
                x / norm, y / norm, z / norm);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is OverflowException || ex is OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Walks a directory-less trailer backwards ([data][id:u16][len:u32] records) for one record.</summary>
    private static byte[] ReadPhotoRecord(FileStream stream, ushort recordId)
    {
        if (stream.Length < FooterLength)
            return null;
        var footer = ReadAt(stream, stream.Length - FooterLength, FooterLength);
        if (Encoding.ASCII.GetString(footer, FooterLength - 32, 32) != Magic)
            return null;

        long trailerLength = BitConverter.ToUInt32(footer, FooterLength - 40);
        if (trailerLength > stream.Length)
            return null;
        var trailerStart = stream.Length - trailerLength;
        var headerAt = stream.Length - FooterLength; // header of the last record
        for (var i = 0; i < 16 && headerAt >= trailerStart; i++)
        {
            var header = ReadAt(stream, headerAt, 6);
            var id = BitConverter.ToUInt16(header, 0);
            long length = BitConverter.ToUInt32(header, 2);
            if (length > headerAt - trailerStart)
                return null;
            if (id == recordId)
                return length > MaxRecordBytes ? null : ReadAt(stream, headerAt - length, (int)length);
            headerAt -= length + 6;
        }
        return null;
    }

    /// <summary>Data of the trailer record with the given directory id, or null.</summary>
    private static byte[] ReadRecord(FileStream stream, ushort dirId)
    {
        if (stream.Length < FooterLength)
            return null;

        var footer = ReadAt(stream, stream.Length - FooterLength, FooterLength);
        if (Encoding.ASCII.GetString(footer, FooterLength - 32, 32) != Magic)
            return null;

        long trailerLength = BitConverter.ToUInt32(footer, FooterLength - 40);
        var lastId = BitConverter.ToUInt16(footer, 0);
        var directoryLength = BitConverter.ToUInt32(footer, 2);
        if (lastId != 0 || trailerLength > stream.Length || directoryLength != 31 * 10)
            return null; // photos have no directory; not handled here

        var trailerStart = stream.Length - trailerLength;
        var directory = ReadAt(stream, stream.Length - FooterLength - directoryLength, (int)directoryLength);
        for (var slot = 0; slot < 31; slot++)
        {
            if (BitConverter.ToUInt16(directory, slot * 10) != dirId)
                continue;
            var size = BitConverter.ToUInt32(directory, slot * 10 + 2);
            var offset = BitConverter.ToUInt32(directory, slot * 10 + 6);
            if (size == 0 || size > MaxRecordBytes || offset + (long)size > trailerLength)
                return null;
            return ReadAt(stream, trailerStart + offset, (int)size);
        }
        return null;
    }

    private static byte[] ReadAt(FileStream stream, long position, int count)
    {
        var buffer = new byte[count];
        stream.Seek(position, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
                throw new IOException("Unexpected end of file");
            read += n;
        }
        return buffer;
    }

    /// <summary>
    /// Accelerometer sample (g) in the viewer frame. Sensor axes map to the viewer frame as (z, -x, y),
    /// for video and photos alike, fitted against the vendor's own horizon levelling on recordings with
    /// the camera upright, sideways and upside down.
    /// </summary>
    private static void Accelerometer(byte[] imu, int offset, out double x, out double y, out double z)
    {
        x = Value(imu, offset, 2);
        y = -Value(imu, offset, 0);
        z = Value(imu, offset, 1);
    }

    /// <summary>Gyroscope sample (rad/s) in the viewer frame.</summary>
    /// <remarks>The axis mapping mirrors the frame (determinant -1), so the rotation vector also changes sign.</remarks>
    private static void Gyroscope(byte[] imu, int offset, out double x, out double y, out double z)
    {
        x = -Value(imu, offset, 5);
        y = Value(imu, offset, 3);
        z = -Value(imu, offset, 4);
    }

    private static double Value(byte[] imu, int offset, int k) => (BitConverter.ToUInt16(imu, offset + 8 + 2 * k) - 0x8000) / 1000.0;

    /// <summary>
    /// Complementary filter: the gyroscope turns "up" with the camera, the accelerometer pulls it back
    /// towards gravity with a 0.5 s time constant. Calls <paramref name="sample"/> with the time (µs) and
    /// "up" after every sample until it returns false.
    /// </summary>
    private static void Track(byte[] imu, Func<double, double, double, double, bool> sample)
    {
        var count = imu.Length / 20;
        double ux = 0, uy = 0, uz = 0;
        double previous = 0;
        var initialised = false;

        for (var i = 0; i < count; i++)
        {
            var o = i * 20;
            var t = (double)BitConverter.ToUInt64(imu, o);
            Accelerometer(imu, o, out var ax, out var ay, out var az);
            Gyroscope(imu, o, out var gx, out var gy, out var gz);

            var norm = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (norm < 1e-6)
                continue;
            ax /= norm;
            ay /= norm;
            az /= norm;

            if (!initialised)
            {
                ux = ax;
                uy = ay;
                uz = az;
                initialised = true;
            }
            else
            {
                var dt = Math.Max(0, (t - previous) / 1e6);
                // a world-fixed vector seen from the rotating camera: du/dt = -ω × u
                double cx = gy * uz - gz * uy, cy = gz * ux - gx * uz, cz = gx * uy - gy * ux;
                ux -= cx * dt;
                uy -= cy * dt;
                uz -= cz * dt;
                var alpha = dt / (SmoothingSeconds + dt);
                ux += alpha * (ax - ux);
                uy += alpha * (ay - uy);
                uz += alpha * (az - uz);
                var len = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                if (!(len > 1e-9) || double.IsInfinity(len))
                {
                    // never let a bad sample poison the rest of the track
                    ux = ax;
                    uy = ay;
                    uz = az;
                    len = 1;
                }
                ux /= len;
                uy /= len;
                uz /= len;
            }
            previous = t;

            if (!sample(t, ux, uy, uz))
                return;
        }
    }

    /// <summary>The filtered "up" every <see cref="OutputIntervalSeconds"/>, as JSON for the video viewer.</summary>
    private static string Filter(byte[] imu, double lastFrameMicros)
    {
        var times = new StringBuilder();
        var ups = new StringBuilder();
        var nextOutput = double.MinValue;
        Track(imu, (t, ux, uy, uz) =>
        {
            if (t < nextOutput)
                return true;
            nextOutput = t + OutputIntervalSeconds * 1e6;
            if (times.Length > 0)
            {
                times.Append(',');
                ups.Append(',');
            }
            times.Append(((t - lastFrameMicros) / 1e6).ToString("0.###", CultureInfo.InvariantCulture));
            ups.Append(ux.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
               .Append(uy.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
               .Append(uz.ToString("0.####", CultureInfo.InvariantCulture));
            return true;
        });

        return times.Length == 0 ? null : $"{{\"tEnd\":[{times}],\"up\":[{ups}]}}";
    }
}
