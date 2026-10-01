# Regenerate sprite.png from the artwork and detect the tablet screen quad.
# The screen is an opaque black panel in this revision. It is found as a
# connected region of opaque near-black pixels and fitted with a minimum-area
# rotated rectangle (convex hull + rotating calipers), which is robust against
# the notch left where a finger used to overlap the screen.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 without a BOM as ANSI.
param(
  [string]$Source = $env:WBPET_SOURCE,
  [string]$Out    = "sprite.png",
  [int]$Size      = 1024,
  [int]$SeedX     = 1990,
  [int]$SeedY     = 2190,
  [int]$DarkMax   = 30
)

if (-not $Source) { throw "no source image: pass -Source or set WBPET_SOURCE" }

Add-Type -AssemblyName System.Drawing

if (-not ('Caliper' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public class Caliper {
    public int W, H, Stride;
    public byte[] B;

    public Caliper(Bitmap b) {
        W = b.Width; H = b.Height;
        Rectangle r = new Rectangle(0, 0, W, H);
        BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Stride = d.Stride;
        B = new byte[Stride * H];
        Marshal.Copy(d.Scan0, B, 0, B.Length);
        b.UnlockBits(d);
    }
    public int A(int x, int y) { return B[y * Stride + x * 4 + 3]; }
    public int L(int x, int y) {
        return (B[y * Stride + x * 4] + B[y * Stride + x * 4 + 1] + B[y * Stride + x * 4 + 2]) / 3;
    }

    static List<double[]> Hull(List<double[]> pts) {
        int n = pts.Count;
        pts.Sort(delegate(double[] a, double[] b) {
            if (a[0] != b[0]) return a[0].CompareTo(b[0]);
            return a[1].CompareTo(b[1]);
        });
        List<double[]> h = new List<double[]>();
        for (int pass = 0; pass < 2; pass++) {
            int start = h.Count;
            for (int i = 0; i < n; i++) {
                int idx = pass == 0 ? i : n - 1 - i;
                double[] p = pts[idx];
                while (h.Count >= start + 2) {
                    double[] a = h[h.Count - 2], b = h[h.Count - 1];
                    double cr = (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]);
                    if (cr <= 0) h.RemoveAt(h.Count - 1); else break;
                }
                h.Add(p);
            }
            h.RemoveAt(h.Count - 1);
        }
        return h;
    }

    public static string Run(Bitmap bmp, int sx, int sy, int darkMax) {
        Caliper f = new Caliper(bmp);
        bool[] seen = new bool[f.W * f.H];
        Stack<int> st = new Stack<int>();
        st.Push(sy * f.W + sx);
        List<double[]> pts = new List<double[]>();
        while (st.Count > 0) {
            int p = st.Pop();
            int x = p % f.W, y = p / f.W;
            if (seen[p]) continue;
            seen[p] = true;
            if (f.A(x, y) < 200 || f.L(x, y) > darkMax) continue;
            pts.Add(new double[] { x, y });
            if (x > 0) st.Push(p - 1);
            if (x < f.W - 1) st.Push(p + 1);
            if (y > 0) st.Push(p - f.W);
            if (y < f.H - 1) st.Push(p + f.W);
        }
        if (pts.Count < 5000) return "region too small: " + pts.Count;

        List<double[]> h = Hull(pts);
        int m = h.Count;
        double bestArea = double.MaxValue;
        double bux = 1, buy = 0, bvx = 0, bvy = 1, minU = 0, maxU = 0, minV = 0, maxV = 0;
        for (int i = 0; i < m; i++) {
            double[] a = h[i], b = h[(i + 1) % m];
            double dx = b[0] - a[0], dy = b[1] - a[1];
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            double ux = dx / len, uy = dy / len, vx = -uy, vy = ux;
            double nU = double.MaxValue, xU = double.MinValue, nV = double.MaxValue, xV = double.MinValue;
            for (int k = 0; k < m; k++) {
                double u = h[k][0] * ux + h[k][1] * uy, v = h[k][0] * vx + h[k][1] * vy;
                if (u < nU) nU = u; if (u > xU) xU = u;
                if (v < nV) nV = v; if (v > xV) xV = v;
            }
            double area = (xU - nU) * (xV - nV);
            if (area < bestArea) {
                bestArea = area;
                bux = ux; buy = uy; bvx = vx; bvy = vy;
                minU = nU; maxU = xU; minV = nV; maxV = xV;
            }
        }
        double cU = (minU + maxU) / 2, cV = (minV + maxV) / 2;
        double hu = (maxU - minU) / 2, hv = (maxV - minV) / 2;
        double[] su = new double[] { -1, 1, 1, -1 };
        double[] sv = new double[] { 1, 1, -1, -1 };
        double[] px = new double[4], py = new double[4];
        for (int i = 0; i < 4; i++) {
            double u = cU + su[i] * hu, v = cV + sv[i] * hv;
            px[i] = u * bux + v * bvx;
            py[i] = u * buy + v * bvy;
        }
        string[] nm = new string[] { "TL", "TR", "BR", "BL" };
        string s = "region pixels=" + pts.Count + " hull=" + m + " area=" + Math.Round(bestArea) + "\n";
        s += "angle=" + Math.Round(Math.Atan2(buy, bux) * 180 / Math.PI, 2) + " deg  size=" +
             Math.Round(maxU - minU, 1) + " x " + Math.Round(maxV - minV, 1) + "\n";
        s += "corners: ";
        for (int i = 0; i < 4; i++) s += nm[i] + "=(" + Math.Round(px[i], 1) + "," + Math.Round(py[i], 1) + ") ";
        return s;
    }
}
'@ -ReferencedAssemblies System.Drawing
}

$src = [System.Drawing.Bitmap]::FromFile($Source)
$sw = $src.Width; $sh = $src.Height
"source: ${sw}x${sh}"

$bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0,0,$Size,$Size), (New-Object System.Drawing.Rectangle 0,0,$sw,$sh), [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

$outPath = Join-Path (Get-Location) $Out
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
"wrote $outPath"

"--- screen on the 1024 build ---"
[Caliper]::Run($bmp, [int]($SeedX * $Size / $sw), [int]($SeedY * $Size / $sh), $DarkMax)

$bmp.Dispose(); $src.Dispose()
