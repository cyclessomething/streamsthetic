using System.IO;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneCam;

/// <summary>
/// Reads the receiver's small preview picture (shared memory "Local\PhoneCamPreview": header with magic 'PCPV',
/// width, height and a sequence number that is odd while a frame is being written, then NV12 pixels) and draws it
/// into a bitmap for the preview box. Layout must match PreviewSink in windows/receiver/main.cpp.
/// </summary>
public sealed class PreviewReader : IDisposable
{
    private readonly string _mappingName;
    public PreviewReader(int slot = 1)
    {
        if (slot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(slot));
        _mappingName = @"Local\PhoneCamPreview" + (slot == 1 ? "" : slot.ToString());
    }
    private const int HeaderSize = 24; // UINT32 magic, width, height, (padding), LONG64 seq
    private const uint Magic = 0x50435056; // 'PCPV'
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private long _lastSeq = -1;
    private byte[] _nv12 = [];
    private byte[] _bgra = [];
    public WriteableBitmap? Bitmap { get; private set; }
    /// <summary>When the last new picture arrived (to tell "no picture right now" apart from a still scene).</summary>
    public DateTime LastFrame { get; private set; } = DateTime.MinValue;

    /// <summary>Copies a new picture into <see cref="Bitmap"/> if there is one. Returns true when it changed.</summary>
    public bool Update()
    {
        if (_view == null && !Open()) return false;
        var seq = _view!.ReadInt64(16);
        if (seq == _lastSeq || (seq & 1) == 1) return false;
        int width = _view.ReadInt32(4), height = _view.ReadInt32(8);
        if (width <= 0 || height <= 0 || width > 1920 || height > 1080) return false;
        var size = width * height * 3 / 2;
        if (_nv12.Length != size) _nv12 = new byte[size];
        _view.ReadArray(HeaderSize, _nv12, 0, size);
        if (_view.ReadInt64(16) != seq) return false; // overwritten while copying: take the next one
        _lastSeq = seq;
        LastFrame = DateTime.Now;

        if (Bitmap == null || Bitmap.PixelWidth != width || Bitmap.PixelHeight != height)
        {
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            _bgra = new byte[width * height * 4];
        }
        Nv12ToBgra(_nv12, _bgra, width, height);
        Bitmap.WritePixels(new Int32Rect(0, 0, width, height), _bgra, width * 4, 0);
        return true;
    }

    private bool Open()
    {
        try
        {
            _file = MemoryMappedFile.OpenExisting(_mappingName, MemoryMappedFileRights.Read);
            _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (_view.ReadUInt32(0) == Magic) return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
        Dispose();
        return false;
    }

    /// <summary>Video-range BT.709 (what the phone's encoder produces) to 8-bit BGR.</summary>
    private static void Nv12ToBgra(byte[] nv12, byte[] bgra, int width, int height)
    {
        var uvStart = width * height;
        for (var y = 0; y < height; y++)
        {
            var uvRow = uvStart + (y / 2) * width;
            for (var x = 0; x < width; x++)
            {
                var c = 1.164f * (nv12[y * width + x] - 16);
                var u = nv12[uvRow + (x & ~1)] - 128;
                var v = nv12[uvRow + (x & ~1) + 1] - 128;
                var o = (y * width + x) * 4;
                bgra[o] = Clamp(c + 2.112f * u);
                bgra[o + 1] = Clamp(c - 0.213f * u - 0.533f * v);
                bgra[o + 2] = Clamp(c + 1.793f * v);
                bgra[o + 3] = 255;
            }
        }
    }

    private static byte Clamp(float value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    public void Dispose()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
        _lastSeq = -1;
        LastFrame = DateTime.MinValue;
        Bitmap = null;
    }
}
