# ASCII only: Windows PowerShell 5.1 decodes BOM-less UTF-8 as ANSI.
#
# Practical note (2026-11-02, measured on a 2048x1152 dashcam-style photo):
#   - For glyphs ~10 px tall, a SMALL factor (4-6x) reads better than a large one
#     (16-20x). High-magnification bicubic turns strokes into smooth blobs; a modest
#     factor keeps the original pixel edges.
#   - Grayscale destroys red-sign / white-glyph contrast: use -Mode red (R - max(G,B)).
#   - Neither trick recovers information that is not in the source. ~10x12 px per
#     Chinese glyph is already below the threshold where strokes separate -- report
#     "unreadable" instead of guessing the characters.
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][int]$X,
    [Parameter(Mandatory=$true)][int]$Y,
    [Parameter(Mandatory=$true)][int]$W,
    [Parameter(Mandatory=$true)][int]$H,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$Scale = 10,
    [double]$Amount = 1.6,
    [int]$Radius = 2,
    [ValidateSet('gray','red')][string]$Mode = 'gray'
)

Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class ImgEnhance
{
    // Crop -> grayscale -> 1..99 percentile stretch -> high-quality upscale -> unsharp mask.
    public static void Run(string src, int x, int y, int w, int h, string dst, int scale, double amount, int radius, bool redMode)
    {
        using (var srcBmp = new Bitmap(src))
        {
            var rect = new Rectangle(
                Math.Max(0, x), Math.Max(0, y),
                Math.Min(w, srcBmp.Width - Math.Max(0, x)),
                Math.Min(h, srcBmp.Height - Math.Max(0, y)));

            using (var crop = srcBmp.Clone(rect, PixelFormat.Format32bppArgb))
            {
                // 1) grayscale + histogram stretch
                int n = crop.Width * crop.Height;
                var gray = new byte[n];
                var data = crop.LockBits(new Rectangle(0, 0, crop.Width, crop.Height),
                                         ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    var buf = new byte[stride * crop.Height];
                    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                    var hist = new int[256];
                    for (int yy = 0; yy < crop.Height; yy++)
                    {
                        int row = yy * stride;
                        for (int xx = 0; xx < crop.Width; xx++)
                        {
                            int i = row + xx * 4;
                            int rr = buf[i + 2], gg2 = buf[i + 1], bb = buf[i];
                            int g;
                            if (redMode)
                            {
                                // Redness: how much the pixel leans red vs its own green/blue.
                                // A red sign with white glyphs becomes bright background + dark glyphs,
                                // which is exactly what grayscale destroys.
                                g = rr - Math.Max(gg2, bb);
                                if (g < 0) g = 0;
                            }
                            else
                            {
                                g = (int)(rr * 0.299 + gg2 * 0.587 + bb * 0.114);
                            }
                            if (g > 255) g = 255;
                            gray[yy * crop.Width + xx] = (byte)g;
                            hist[g]++;
                        }
                    }
                    // percentile bounds (1% / 99%)
                    int lowCut = (int)(n * 0.01), highCut = (int)(n * 0.99);
                    int acc = 0, lo = 0, hi = 255;
                    for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= lowCut) { lo = v; break; } }
                    acc = 0;
                    for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= highCut) { hi = v; break; } }
                    if (hi - lo < 8) { lo = 0; hi = 255; }
                    double span = hi - lo;

                    // 2) upscale to a working bitmap (bicubic-like via GDI+)
                    int upW = crop.Width * scale, upH = crop.Height * scale;
                    using (var up = new Bitmap(upW, upH, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(up))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            using (var stretched = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb))
                            {
                                for (int i = 0; i < n; i++)
                                {
                                    int v = (int)((gray[i] - lo) * 255.0 / span);
                                    if (v < 0) v = 0; if (v > 255) v = 255;
                                    int px = i % crop.Width, py = i / crop.Width;
                                    stretched.SetPixel(px, py, Color.FromArgb(v, v, v));
                                }
                                g.DrawImage(stretched, new Rectangle(0, 0, upW, upH));
                            }
                        }

                        // 3) unsharp mask on the upscaled gray image
                        var outp = new Bitmap(upW, upH, PixelFormat.Format32bppArgb);
                        var d2 = up.LockBits(new Rectangle(0, 0, upW, upH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        var d3 = outp.LockBits(new Rectangle(0, 0, upW, upH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            var big = new byte[d2.Stride * upH];
                            Marshal.Copy(d2.Scan0, big, 0, big.Length);
                            var res = new byte[d3.Stride * upH];
                            var gv = new int[upW * upH];
                            for (int yy = 0; yy < upH; yy++)
                            {
                                int row = yy * d2.Stride;
                                for (int xx = 0; xx < upW; xx++) gv[yy * upW + xx] = big[row + xx * 4 + 1];
                            }
                            for (int yy = 0; yy < upH; yy++)
                            {
                                int row = yy * d3.Stride;
                                for (int xx = 0; xx < upW; xx++)
                                {
                                    int sum = 0, cnt = 0;
                                    for (int dy = -radius; dy <= radius; dy++)
                                    {
                                        int ny = yy + dy; if (ny < 0 || ny >= upH) continue;
                                        for (int dx = -radius; dx <= radius; dx++)
                                        {
                                            int nx = xx + dx; if (nx < 0 || nx >= upW) continue;
                                            sum += gv[ny * upW + nx]; cnt++;
                                        }
                                    }
                                    double blur = (double)sum / cnt;
                                    double orig = gv[yy * upW + xx];
                                    int val = (int)(orig + amount * (orig - blur));
                                    if (val < 0) val = 0; if (val > 255) val = 255;
                                    int i = row + xx * 4;
                                    res[i] = (byte)val; res[i + 1] = (byte)val; res[i + 2] = (byte)val; res[i + 3] = 255;
                                }
                            }
                            Marshal.Copy(res, 0, d3.Scan0, res.Length);
                        }
                        finally
                        {
                            up.UnlockBits(d2);
                            outp.UnlockBits(d3);
                        }
                        outp.Save(dst, ImageFormat.Png);
                        outp.Dispose();
                    }
                }
                finally { crop.UnlockBits(data); }
            }
        }
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing

[ImgEnhance]::Run($Source, $X, $Y, $W, $H, $Out, $Scale, $Amount, $Radius, ($Mode -eq 'red'))
Write-Output ("WROTE " + $Out + " mode=" + $Mode)
