using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class ComparisonCutArtwork
{
    const int Scale = 8;
    const int Side = 32;
    const int Pixels = Side * Scale;
    static Bitmap Layer(string path, RectangleF source, double center, float size)
    {
        var layer = new Bitmap(Pixels, Pixels, PixelFormat.Format32bppArgb);
        using (var image = new Bitmap(path))
        using (var canvas = Graphics.FromImage(layer))
        using (var sample = new ImageAttributes())
        {
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            canvas.PixelOffsetMode = PixelOffsetMode.Half;
            sample.SetWrapMode(WrapMode.TileFlipXY);
            float start = (float)((center - size / 2.0) * Scale);
            float width = size * Scale;
            var corners = new[] { new PointF(start,start), new PointF(start+width,start), new PointF(start,start+width) };
            canvas.DrawImage(image,corners,source,GraphicsUnit.Pixel,sample);
        }
        return layer;
    }

    public static void Make(string folder, string destination, double gap)
    {
        double rootTwo = Math.Sqrt(2);
        double shift = gap / (2 * rootTwo);
        using (var gpt = Layer(Path.Combine(folder,"chatgpt.png"), new RectangleF(0,0,96,96), Side/2.0-shift,30.3f))
        using (var claude = Layer(Path.Combine(folder,"claude-mark.png"),new RectangleF(.5f,1.5f,68,68), Side/2.0+shift,30.6f))
        using (var mark = new Bitmap(Pixels,Pixels,PixelFormat.Format32bppArgb))
        {
            for (int y=0; y<Pixels; y++)
            for (int x=0; x<Pixels; x++)
            {
                double signed = (x+y+1-Pixels)/rootTwo;
                double coverage = Math.Max(0,Math.Min(1,Math.Abs(signed)-gap*Scale/2+.5));
                byte alpha = (signed<0 ? gpt : claude).GetPixel(x,y).A;
                mark.SetPixel(x,y,Color.FromArgb((int)Math.Round(alpha*coverage),0,0,0));
            }
            mark.Save(destination,ImageFormat.Png);
        }
    }
}
internal static class Program { static void Main(string[] args) { ComparisonCutArtwork.Make(args[0],args[1],1.65); } }
