# Converts the Oracooll artwork (assets\OrclWAMP.png) into assets\OrclWAMP.ico (PNG-compressed, 16-256 px).
# The white background around the artwork is made transparent (flood fill from the edges, so the white
# laptop screens stay white) and the image is cropped to the artwork before scaling.
param([string]$Source = (Join-Path $PSScriptRoot '..\assets\OrclWAMP.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class IconPrep {
    static bool IsBg(int argb) { var c = Color.FromArgb(argb); return c.A < 16 || (c.R > 235 && c.G > 235 && c.B > 235); }
    public static Bitmap Prepare(string path) {
        Bitmap src; using (var tmp = new Bitmap(path)) src = tmp.Clone(new Rectangle(0, 0, tmp.Width, tmp.Height), PixelFormat.Format32bppArgb);
        int w = src.Width, h = src.Height;
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new int[w * h]; Marshal.Copy(data.Scan0, px, 0, px.Length);
        var seen = new bool[w * h]; var q = new Queue<int>();
        for (int x = 0; x < w; x++) { q.Enqueue(x); q.Enqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { q.Enqueue(y * w); q.Enqueue(y * w + w - 1); }
        while (q.Count > 0) {
            int i = q.Dequeue(); if (seen[i] || !IsBg(px[i])) continue;
            seen[i] = true; px[i] = 0;
            int x = i % w, y = i / w;
            if (x > 0) q.Enqueue(i - 1); if (x < w - 1) q.Enqueue(i + 1);
            if (y > 0) q.Enqueue(i - w); if (y < h - 1) q.Enqueue(i + w);
        }
        int minX = w, minY = h, maxX = 0, maxY = 0;
        for (int i = 0; i < px.Length; i++) if (((uint)px[i] >> 24) > 0) { int x = i % w, y = i / w; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        Marshal.Copy(px, 0, data.Scan0, px.Length); src.UnlockBits(data);
        int cw = maxX - minX + 1, ch = maxY - minY + 1, side = (int)(Math.Max(cw, ch) * 1.04);
        var sq = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sq)) {
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle((side - cw) / 2, (side - ch) / 2, cw, ch), new Rectangle(minX, minY, cw, ch), GraphicsUnit.Pixel);
        }
        src.Dispose(); return sq;
    }
}
"@

$out = Join-Path $PSScriptRoot '..\assets\OrclWAMP.ico'
$master = [IconPrep]::Prepare((Resolve-Path $Source).Path)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($master, 0, 0, $s, $s); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)   # Vista+ PNG frame for the big size
    } else {
        # Classic 32-bit DIB frame: BITMAPINFOHEADER, bottom-up BGRA pixels, then an (unused) AND mask.
        $w = New-Object System.IO.BinaryWriter $ms
        $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $w.Write((New-Object byte[] ($maskRow * $s)))
        $w.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}
# Small transparent logo embedded in the exe for the window header.
$logo = New-Object System.Drawing.Bitmap 128, 128
$lg = [System.Drawing.Graphics]::FromImage($logo); $lg.InterpolationMode = 'HighQualityBicubic'; $lg.PixelOffsetMode = 'HighQuality'
$lg.DrawImage($master, 0, 0, 128, 128); $lg.Dispose()
$logo.Save((Join-Path $PSScriptRoot '..\assets\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $logo.Dispose()
$master.Dispose()
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length; $b = [byte]($(if ($s -ge 256) { 0 } else { $s }))
    $bw.Write($b); $bw.Write($b); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $out"
