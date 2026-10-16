using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public enum ChipGlyph { Connected, Disconnected, Busy, Error }

// Flat, single-colour computer-chip icon, transparent background.
//   Connected     solid chip: die ring, solder bumps and core cut out
//   Disconnected  outlined chip with a slash through it
//   Busy          outlined chip with a solid core (blinks with Connected)
//   Error         solid chip with a "!" cut out
// 24 px and up use the detailed 32-unit design; smaller sizes use a simpler 16-unit one that stays legible.
public static class ChipIcon
{
    public static Bitmap Render(int size, ChipGlyph glyph, Color fg)
    {
        if (size < 24) return RenderSimple(size, glyph, fg);

        // Draw pixel-crisp at 4x, then box-average down: exact at 32/64 px, anti-aliased at in-between sizes.
        using (var big = RenderDetailed(size * 4, glyph, fg))
            return Downsample(big, 4);
    }

    static Bitmap Downsample(Bitmap src, int f)
    {
        int sw = src.Width, w = sw / f;
        var rect = new Rectangle(0, 0, sw, sw);
        var px = new int[sw * sw];
        var sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, px, 0, px.Length);
        src.UnlockBits(sd);

        var outPx = new int[w * w];
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                long a = 0, r = 0, g = 0, b = 0;
                for (int dy = 0; dy < f; dy++)
                    for (int dx = 0; dx < f; dx++)
                    {
                        int c = px[(y * f + dy) * sw + x * f + dx];
                        int ca = (c >> 24) & 0xFF;
                        a += ca;
                        r += ((c >> 16) & 0xFF) * ca;
                        g += ((c >> 8) & 0xFF) * ca;
                        b += (c & 0xFF) * ca;
                    }
                if (a == 0) continue;
                int n = f * f;
                outPx[y * w + x] = (int)((a / n) << 24 | (r / a) << 16 | (g / a) << 8 | (b / a));
            }

        var dst = new Bitmap(w, w, PixelFormat.Format32bppArgb);
        var dd = dst.LockBits(new Rectangle(0, 0, w, w), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(outPx, 0, dd.Scan0, outPx.Length);
        dst.UnlockBits(dd);
        return dst;
    }

    // 32-unit grid: body 6..26 with a chamfered pin-1 corner, four pins per side.
    static Bitmap RenderDetailed(int px, ChipGlyph glyph, Color fg)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        float k = px / 32f;
        using (var g = Graphics.FromImage(bmp))
        using (var ink = new SolidBrush(fg))
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;

            foreach (float a in new float[] { 9, 13, 17, 21 })
            {
                g.FillRectangle(ink, R(k, a, 2, a + 2, 6));
                g.FillRectangle(ink, R(k, a, 26, a + 2, 30));
                g.FillRectangle(ink, R(k, 2, a, 6, a + 2));
                g.FillRectangle(ink, R(k, 26, a, 30, a + 2));
            }
            g.FillPolygon(ink, Body(k, 6, 26, 3));

            g.CompositingMode = CompositingMode.SourceCopy;
            switch (glyph)
            {
                case ChipGlyph.Connected:
                    Ring(g, clear, k, 9, 23);                               // die edge
                    foreach (float x in new float[] { 11, 20 })              // solder bumps
                        foreach (float y in new float[] { 11, 20 })
                            g.FillRectangle(clear, R(k, x, y, x + 1, y + 1));
                    Ring(g, clear, k, 13, 19);                              // core
                    break;

                case ChipGlyph.Error:
                    Ring(g, clear, k, 9, 23);
                    g.FillRectangle(clear, R(k, 15, 12, 17, 17));
                    g.FillRectangle(clear, R(k, 15, 19, 17, 21));
                    break;

                case ChipGlyph.Busy:
                case ChipGlyph.Disconnected:
                    g.FillPolygon(clear, Body(k, 8, 24, 2));                 // hollow body
                    g.CompositingMode = CompositingMode.SourceOver;
                    Ring(g, ink, k, 11, 21);                                // die outline
                    if (glyph == ChipGlyph.Busy) g.FillRectangle(ink, R(k, 14, 14, 18, 18));
                    break;
            }

            if (glyph == ChipGlyph.Disconnected)
            {
                var a = new PointF(3 * k, 29 * k);
                var b = new PointF(29 * k, 3 * k);
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var halo = new Pen(Color.Transparent, 6 * k)) g.DrawLine(halo, a, b);
                g.CompositingMode = CompositingMode.SourceOver;
                using (var pen = new Pen(fg, 2 * k)) g.DrawLine(pen, a, b);
            }
        }
        return bmp;
    }

    // Square from a..b with the top-left corner cut by c (the pin-1 mark).
    static PointF[] Body(float k, float a, float b, float c)
    {
        return new[]
        {
            new PointF((a + c) * k, a * k), new PointF(b * k, a * k), new PointF(b * k, b * k),
            new PointF(a * k, b * k), new PointF(a * k, (a + c) * k),
        };
    }

    // One-unit-wide square ring from a..b.
    static void Ring(Graphics g, Brush brush, float k, float a, float b)
    {
        g.FillRectangle(brush, R(k, a, a, b, a + 1));
        g.FillRectangle(brush, R(k, a, b - 1, b, b));
        g.FillRectangle(brush, R(k, a, a, a + 1, b));
        g.FillRectangle(brush, R(k, b - 1, a, b, b));
    }

    static Bitmap RenderSimple(int size, ChipGlyph glyph, Color fg)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        float k = size / 16f;
        int t = Math.Max(1, (int)Math.Round(k));   // stroke width in pixels
        using (var g = Graphics.FromImage(bmp))
        using (var ink = new SolidBrush(fg))
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;

            // pins: two per side
            foreach (float p in new float[] { 5, 9 })
            {
                g.FillRectangle(ink, R(k, p, 1, p + 2, 3));
                g.FillRectangle(ink, R(k, p, 13, p + 2, 15));
                g.FillRectangle(ink, R(k, 1, p, 3, p + 2));
                g.FillRectangle(ink, R(k, 13, p, 15, p + 2));
            }
            Rectangle body = R(k, 3, 3, 13, 13);
            g.FillRectangle(ink, body);

            g.CompositingMode = CompositingMode.SourceCopy;
            switch (glyph)
            {
                case ChipGlyph.Connected:
                    g.FillRectangle(clear, R(k, 5, 5, 7, 7));
                    break;
                case ChipGlyph.Error:
                    g.FillRectangle(clear, R(k, 7, 5, 9, 9));
                    g.FillRectangle(clear, R(k, 7, 10, 9, 12));
                    break;
                case ChipGlyph.Busy:
                case ChipGlyph.Disconnected:
                    g.FillRectangle(clear, Rectangle.Inflate(body, -t, -t));
                    if (glyph == ChipGlyph.Busy)
                    {
                        g.CompositingMode = CompositingMode.SourceOver;
                        g.FillRectangle(ink, R(k, 7, 7, 9, 9));
                    }
                    break;
            }

            if (glyph == ChipGlyph.Disconnected)
            {
                var a = new Point((int)Math.Round(1 * k), (int)Math.Round(15 * k) - 1);
                var b = new Point((int)Math.Round(15 * k) - 1, (int)Math.Round(1 * k));
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var halo = new Pen(Color.Transparent, t * 3)) g.DrawLine(halo, a, b);
                g.CompositingMode = CompositingMode.SourceOver;
                using (var pen = new Pen(fg, t)) g.DrawLine(pen, a, b);
            }
        }
        return bmp;
    }

    // App / installer icon: white chip on a dark rounded tile, readable on any background.
    public static Bitmap RenderTile(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = size * 0.22f, e = size - 0.5f;
            using (var path = new GraphicsPath())
            using (var fill = new SolidBrush(Color.FromArgb(28, 28, 28)))
            {
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(e - r * 2, 0, r * 2, r * 2, 270, 90);
                path.AddArc(e - r * 2, e - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(0, e - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(fill, path);
            }
            int inner = (int)Math.Round(size * 0.72);
            int off = (size - inner) / 2;
            using (var chip = Render(inner, ChipGlyph.Connected, Color.White))
                g.DrawImage(chip, new Rectangle(off, off, inner, inner));
        }
        return bmp;
    }

    // Writes a multi-size .ico with PNG-compressed entries.
    public static void SaveIco(string path, int[] sizes, Func<int, Bitmap> render)
    {
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }

    static Rectangle R(float k, float x0, float y0, float x1, float y1)
    {
        return Rectangle.FromLTRB((int)Math.Round(x0 * k), (int)Math.Round(y0 * k),
                                  (int)Math.Round(x1 * k), (int)Math.Round(y1 * k));
    }
}
