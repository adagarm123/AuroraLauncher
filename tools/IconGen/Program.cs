using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// IconGen <source.png> <dest.ico>
// Crops the source to a centered square, renders multiple sizes, wraps them
// into a Windows .ico container (PNG-compressed entries).
if (args.Length < 2) { Console.Error.WriteLine("usage: IconGen <src.png> <dest.ico>"); return 1; }

var srcPath = Path.GetFullPath(args[0]);
var destPath = Path.GetFullPath(args[1]);

var src = new BitmapImage();
using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read))
{
    src.BeginInit();
    src.CacheOption = BitmapCacheOption.OnLoad;
    src.StreamSource = fs;
    src.EndInit();
}
src.Freeze();

// Center-crop to square, then inset 10% per side to strip any white margin
// that the source image may carry around the rounded icon.
int side = Math.Min(src.PixelWidth, src.PixelHeight);
const double inset = 0.10;
var crop = new CroppedBitmap(src, new Int32Rect((src.PixelWidth - side) / 2, (src.PixelHeight - side) / 2, side, side));
var brush = new ImageBrush(crop)
{
    Stretch = Stretch.UniformToFill,
    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
    Viewbox = new Rect(inset, inset, 1 - 2 * inset, 1 - 2 * inset)
};

int[] sizes = [256, 128, 64, 48, 32, 16];
var blobs = new List<byte[]>();
foreach (var s in sizes)
{
    var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
    var rect = new Rect(0, 0, s, s);
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        // Rounded corners so the icon looks like the source's rounded square.
        double radius = s * 0.22;
        dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
    }
    rtb.Render(dv);

    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(rtb));
    using var ms = new MemoryStream();
    enc.Save(ms);
    blobs.Add(ms.ToArray());
}

using var outStream = new FileStream(destPath, FileMode.Create, FileAccess.Write);
using var bw = new BinaryWriter(outStream);
bw.Write((ushort)0);
bw.Write((ushort)1);                       // type: icon
bw.Write((ushort)blobs.Count);
uint offset = 6 + 16 * (uint)blobs.Count;
for (int i = 0; i < blobs.Count; i++)
{
    int s = sizes[i];
    bw.Write((byte)(s >= 256 ? 0 : s));    // width
    bw.Write((byte)(s >= 256 ? 0 : s));    // height
    bw.Write((byte)0);                     // palette
    bw.Write((byte)0);                     // reserved
    bw.Write((ushort)1);                   // planes
    bw.Write((ushort)32);                  // bpp
    bw.Write((uint)blobs[i].Length);
    bw.Write(offset);
    offset += (uint)blobs[i].Length;
}
foreach (var b in blobs) bw.Write(b);

Console.WriteLine($"ICO written: {new FileInfo(destPath).Length} bytes ({blobs.Count} sizes)");
return 0;
