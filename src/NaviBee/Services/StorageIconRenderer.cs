using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MusicBeePlugin
{
    internal static class StorageIconRenderer
    {
        private const int BaseIconSize = 16;
        private const int SupersampleFactor = 4;

        internal static Bitmap RenderForCurrentDpi()
        {
            float dpi = DetectHostDpi();
            int size = ComputeIconSize(dpi);
            return Render(size, dpi);
        }

        internal static float DetectHostDpi()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g.DpiX >= 72f && g.DpiX <= 480f)
                        return g.DpiX;
                }
            }
            catch
            {
            }
            return 96f;
        }

        internal static int ComputeIconSize(float dpi)
        {
            if (dpi > 100f)
                return Math.Max(BaseIconSize, (int)Math.Round(BaseIconSize * (dpi / 96f)));
            return BaseIconSize;
        }

        internal static Bitmap Render(int pixelSize, float dpi)
        {
            int targetSize = Math.Max(BaseIconSize, pixelSize);
            int hiSize = targetSize * SupersampleFactor;

            using (var hi = new Bitmap(hiSize, hiSize, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.Clear(Color.Transparent);

                    DrawNaviBeeGlyph(g, hiSize);
                }

                var result = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppPArgb);
                result.SetResolution(dpi > 0f ? dpi : 96f, dpi > 0f ? dpi : 96f);

                using (var gOut = Graphics.FromImage(result))
                {
                    gOut.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gOut.SmoothingMode = SmoothingMode.HighQuality;
                    gOut.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    gOut.CompositingQuality = CompositingQuality.HighQuality;
                    gOut.Clear(Color.Transparent);

                    using (var attrs = new ImageAttributes())
                    {
                        attrs.SetWrapMode(WrapMode.TileFlipXY);
                        gOut.DrawImage(
                            hi,
                            new Rectangle(0, 0, targetSize, targetSize),
                            0, 0, hiSize, hiSize,
                            GraphicsUnit.Pixel,
                            attrs);
                    }
                }

                return result;
            }
        }

        private static void DrawNaviBeeGlyph(Graphics g, float s)
        {
            float cx = s * 0.5f;
            float cy = s * 0.5f;

            // 1. Outer Navidrome-blue vinyl disc (fills the 16px slot with a crisp 0.5px AA margin)
            float discPad = s * 0.035f;
            float discDiam = s - discPad * 2f;
            float discRadius = discDiam * 0.5f;
            var discRect = new RectangleF(discPad, discPad, discDiam, discDiam);

            using (var discBrush = new LinearGradientBrush(
                new PointF(discPad, discPad),
                new PointF(s - discPad, s - discPad),
                Color.FromArgb(255, 22, 154, 246),
                Color.FromArgb(255, 0, 106, 198)))
            {
                g.FillEllipse(discBrush, discRect);
            }

            // Subtle dark outer rim so the disc edge stays crisp on light & dark skins alike
            using (var rimPen = new Pen(Color.FromArgb(70, 10, 22, 38), s * 0.025f))
            {
                g.DrawEllipse(rimPen, discRect);
            }

            // 2. Subtle vinyl groove sheen arcs (top-left & bottom-right, matching navibee_icon.png)
            DrawVinylArc(g, cx, cy, discRadius * 0.80f, 198f, 64f, s * 0.032f, Color.FromArgb(115, 130, 212, 255));
            DrawVinylArc(g, cx, cy, discRadius * 0.68f, 204f, 52f, s * 0.026f, Color.FromArgb(80, 130, 212, 255));
            DrawVinylArc(g, cx, cy, discRadius * 0.80f, 18f, 64f, s * 0.032f, Color.FromArgb(115, 130, 212, 255));
            DrawVinylArc(g, cx, cy, discRadius * 0.68f, 24f, 52f, s * 0.026f, Color.FromArgb(80, 130, 212, 255));

            // 3. Center golden bee hexagon (pointy-top, crisp dark border separating gold from blue)
            float hexRadius = discRadius * 0.58f;
            PointF[] hexPoints = BuildPointyHexagon(cx, cy, hexRadius);

            using (var hexPath = new GraphicsPath())
            {
                hexPath.AddPolygon(hexPoints);
                var hexBounds = hexPath.GetBounds();

                using (var hexBrush = new LinearGradientBrush(
                    new PointF(hexBounds.Left, hexBounds.Top),
                    new PointF(hexBounds.Right, hexBounds.Bottom),
                    Color.FromArgb(255, 255, 202, 46),
                    Color.FromArgb(255, 240, 164, 14)))
                {
                    g.FillPath(hexBrush, hexPath);
                }

                using (var hexBorder = new Pen(Color.FromArgb(245, 16, 24, 36), s * 0.044f))
                {
                    hexBorder.LineJoin = LineJoin.Round;
                    g.DrawPath(hexBorder, hexPath);
                }
            }

            // 4. Bold dark-navy eighth note centered inside the hexagon
            DrawMusicNote(g, cx, cy, hexRadius);
        }

        private static void DrawVinylArc(
            Graphics g,
            float cx,
            float cy,
            float radius,
            float startAngle,
            float sweepAngle,
            float width,
            Color color)
        {
            var rect = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);
            using (var pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, rect, startAngle, sweepAngle);
            }
        }

        private static PointF[] BuildPointyHexagon(float cx, float cy, float radius)
        {
            var pts = new PointF[6];
            for (int i = 0; i < 6; i++)
            {
                double rad = (Math.PI / 180.0) * (60.0 * i - 90.0);
                pts[i] = new PointF(
                    cx + (float)(radius * Math.Cos(rad)),
                    cy + (float)(radius * Math.Sin(rad)));
            }
            return pts;
        }

        private static void DrawMusicNote(Graphics g, float cx, float cy, float r)
        {
            Color ink = Color.FromArgb(255, 16, 24, 36);
            float stemX = cx + r * 0.04f;
            float stemTop = cy - r * 0.44f;
            float stemBottom = cy + r * 0.28f;
            float stemWidth = r * 0.24f;

            // Stem
            using (var stemPen = new Pen(ink, stemWidth))
            {
                stemPen.StartCap = LineCap.Round;
                stemPen.EndCap = LineCap.Round;
                g.DrawLine(stemPen, stemX, stemBottom, stemX, stemTop);
            }

            // Open crisp eighth-note flag sweeping right and downward (matches navibee_icon.png)
            using (var flagPath = new GraphicsPath())
            {
                flagPath.AddBezier(
                    new PointF(stemX, stemTop),
                    new PointF(stemX + r * 0.26f, stemTop + r * 0.06f),
                    new PointF(stemX + r * 0.44f, stemTop + r * 0.22f),
                    new PointF(stemX + r * 0.42f, stemTop + r * 0.42f));

                using (var flagPen = new Pen(ink, stemWidth * 0.94f))
                {
                    flagPen.StartCap = LineCap.Round;
                    flagPen.EndCap = LineCap.Round;
                    g.DrawPath(flagPen, flagPath);
                }
            }

            // Tilted oval notehead at lower-left of the stem
            GraphicsState state = g.Save();
            try
            {
                float headCx = stemX - r * 0.22f;
                float headCy = stemBottom + r * 0.04f;
                g.TranslateTransform(headCx, headCy);
                g.RotateTransform(-24f);
                float headW = r * 0.60f;
                float headH = r * 0.45f;
                using (var headBrush = new SolidBrush(ink))
                {
                    g.FillEllipse(headBrush, -headW * 0.5f, -headH * 0.5f, headW, headH);
                }
            }
            finally
            {
                g.Restore(state);
            }
        }
    }
}
