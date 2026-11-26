using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit
{
    // Four layers: graphite substrate, continuous emission, one fixed lens mask,
    // and a transparent glass coating. Lens geometry never follows the gaze.
    internal sealed class LedFaceRenderer
    {
        private const double FaceSize = 88;
        private const double SurfaceRadius = 36.5;
        private const double EyeRadius = 10.7;
        private const double EyeRingRadius = 12.9;
        private const double TimerRadius = 32;
        private static readonly Point Center = new Point(44, 44);
        private static readonly Rect Bounds = new Rect(0, 0, FaceSize, FaceSize);
        private static readonly Color Lilac = Color.FromRgb(177, 155, 211);
        private static readonly Color Teal = Color.FromRgb(112, 206, 193);
        private static readonly ImageBrush LensMask = CreateLensMask();
        private static readonly BitmapSource BaseLayer = CreateBase();
        private static readonly BitmapSource GlassLayer = CreateGlass();
        private static readonly Brush EyeLight = CreateEyeLight();
        private static readonly Brush TimerTextLight = Solid(Color.FromRgb(156, 224, 213));
        private static readonly Brush CompletionLight = Solid(Color.FromRgb(157, 223, 198));
        private static readonly Geometry CompletionMark = CreateCheck();
        private static readonly Typeface TimerTypeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private string cachedTime;
        private Geometry cachedTimeGeometry;

        internal void Draw(DrawingContext dc, Point gazeOffset, double openness, double orbitAngle,
            double timerMix, double remainingRatio, string timeText, bool paused, bool completed)
        {
            if (dc == null) throw new ArgumentNullException("dc");
            double mix = Unit(timerMix), ratio = Unit(remainingRatio), open = Unit(openness);
            double gx = Finite(gazeOffset.X), gy = Finite(gazeOffset.Y);
            double gazeLength = Math.Sqrt(gx * gx + gy * gy);
            if (gazeLength > 14) { gx *= 14 / gazeLength; gy *= 14 / gazeLength; }
            Point eyeCenter = new Point(Center.X + gx, Center.Y + gy);

            dc.DrawImage(BaseLayer, Bounds);
            // Absolute 88-DIP mask coordinates prevent any moving shape from
            // rescaling or translating the honeycomb sampling array.
            dc.PushOpacityMask(LensMask);
            dc.DrawRectangle(Brushes.Transparent, null, Bounds);
            if (completed)
            {
                dc.DrawEllipse(null, Stroke(CompletionLight, 1.8), Center, TimerRadius, TimerRadius);
                dc.DrawGeometry(null, Stroke(CompletionLight, 3.8), CompletionMark);
            }
            else
            {
                if (mix < 1 && open > 0.015)
                {
                    dc.PushOpacity((1 - mix) * Math.Min(1, open * 2));
                    dc.DrawEllipse(EyeLight, null, eyeCenter, EyeRadius, EyeRadius * open);
                    dc.Pop();
                }

                Point ringCenter = new Point(eyeCenter.X + (Center.X - eyeCenter.X) * mix,
                    eyeCenter.Y + (Center.Y - eyeCenter.Y) * mix);
                double radius = EyeRingRadius + (TimerRadius - EyeRingRadius) * mix;
                Color ringColor = Blend(Lilac, Teal, mix);
                double brightness = paused ? 0.65 : 1;
                Brush ringLight = Solid(ringColor);
                dc.PushOpacity(brightness);
                if (mix > 0)
                {
                    dc.PushOpacity(0.15 * mix);
                    dc.DrawEllipse(null, Stroke(ringLight, 1.7), ringCenter, radius, radius);
                    dc.Pop();
                }
                double sweep = 1 - mix + mix * ratio;
                DrawArc(dc, ringCenter, radius, sweep, Stroke(ringLight, 1.65 + 0.25 * mix));

                // Idle orbit is supplied by the animation controller. At the end
                // of the morph its light marks the remaining-time arc endpoint.
                double idleAngle = Finite(orbitAngle);
                double timerAngle = ratio * Math.PI * 2 - Math.PI / 2;
                double angleDelta = Math.Atan2(Math.Sin(timerAngle - idleAngle), Math.Cos(timerAngle - idleAngle));
                double beadAngle = idleAngle + angleDelta * mix;
                if (sweep > 0)
                {
                    Point bead = new Point(ringCenter.X + radius * Math.Cos(beadAngle),
                        ringCenter.Y + radius * Math.Sin(beadAngle));
                    dc.DrawEllipse(Solid(Blend(Color.FromRgb(224, 205, 248), Color.FromRgb(181, 239, 225), mix)),
                        null, bead, 1.95, 1.95);
                }

                if (mix > 0.15 && !String.IsNullOrEmpty(timeText))
                {
                    EnsureTimeGeometry(timeText);
                    dc.PushOpacity(Unit((mix - 0.15) / 0.85));
                    dc.DrawGeometry(TimerTextLight, null, cachedTimeGeometry);
                    dc.Pop();
                }
                dc.Pop();
            }
            dc.Pop();
            // The glass remains above the emitted light, including during blinks.
            dc.DrawImage(GlassLayer, Bounds);
        }

        private void EnsureTimeGeometry(string text)
        {
            if (text == cachedTime && cachedTimeGeometry != null) return;
            FormattedText formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, TimerTypeface, 17, Brushes.White, 1.0);
            Geometry glyphs = formatted.BuildGeometry(new Point());
            Rect bounds = glyphs.Bounds;
            double scale = bounds.Width > 55 ? 55 / bounds.Width : 1;
            Matrix placement = new Matrix(scale, 0, 0, scale,
                Center.X - (bounds.Left + bounds.Width / 2) * scale,
                Center.Y - (bounds.Top + bounds.Height / 2) * scale);
            glyphs.Transform = new MatrixTransform(placement);
            glyphs.Freeze();
            cachedTimeGeometry = glyphs;
            cachedTime = text;
        }

        private static ImageBrush CreateLensMask()
        {
            DrawingGroup lenses = new DrawingGroup();
            using (DrawingContext dc = lenses.Open())
            {
                const double pitch = 1.65;
                double rowPitch = pitch * Math.Sqrt(3) / 2;
                // This is the only bead loop. It runs once, not on animation frames.
                for (int row = -26; row <= 26; row++)
                for (int col = -24; col <= 24; col++)
                {
                    double x = (col + ((row & 1) == 0 ? 0 : 0.5)) * pitch;
                    double y = row * rowPitch;
                    double distance = Math.Sqrt(x * x + y * y);
                    if (distance > SurfaceRadius - 0.8) continue;
                    double radius = 0.50 + 0.13 * (1 - distance / SurfaceRadius);
                    dc.DrawEllipse(Brushes.White, null, new Point(Center.X + x, Center.Y + y), radius, radius);
                }
            }
            lenses.Freeze();
            ImageBrush mask = new ImageBrush(Rasterize(lenses));
            mask.ViewboxUnits = BrushMappingMode.Absolute;
            mask.Viewbox = Bounds;
            mask.ViewportUnits = BrushMappingMode.Absolute;
            mask.Viewport = Bounds;
            mask.Stretch = Stretch.Fill;
            mask.TileMode = TileMode.None;
            mask.Freeze();
            return mask;
        }

        private static BitmapSource CreateBase()
        {
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext dc = drawing.Open())
            {
                RadialGradientBrush graphite = new RadialGradientBrush(Color.FromRgb(30, 33, 42), Color.FromRgb(19, 22, 29));
                graphite.GradientOrigin = new Point(0.32, 0.22);
                graphite.Center = new Point(0.5, 0.5);
                graphite.RadiusX = graphite.RadiusY = 0.66;
                graphite.Freeze();
                dc.DrawEllipse(graphite, null, Center, SurfaceRadius, SurfaceRadius);
                dc.PushOpacityMask(LensMask);
                // Unlit lenses are visible only as a very faint physical texture.
                dc.DrawRectangle(Solid(Color.FromArgb(32, 70, 70, 88)), null, Bounds);
                dc.Pop();
            }
            drawing.Freeze();
            return Rasterize(drawing);
        }

        private static BitmapSource CreateGlass()
        {
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext dc = drawing.Open())
            {
                LinearGradientBrush top = new LinearGradientBrush();
                top.StartPoint = new Point(0, 0);
                top.EndPoint = new Point(0, 1);
                top.GradientStops.Add(new GradientStop(Color.FromArgb(15, 206, 201, 224), 0));
                top.GradientStops.Add(new GradientStop(Color.FromArgb(5, 176, 173, 201), 0.18));
                top.GradientStops.Add(new GradientStop(Color.FromArgb(0, 176, 173, 201), 0.46));
                top.Freeze();
                dc.DrawEllipse(top, null, Center, SurfaceRadius - 0.5, SurfaceRadius - 0.5);
                LinearGradientBrush lower = new LinearGradientBrush(Color.FromArgb(0, 88, 107, 128),
                    Color.FromArgb(5, 88, 107, 128), new Point(0, 0.78), new Point(0, 1));
                lower.Freeze();
                dc.DrawEllipse(lower, null, Center, SurfaceRadius - 0.5, SurfaceRadius - 0.5);
                RadialGradientBrush glint = new RadialGradientBrush();
                glint.GradientStops.Add(new GradientStop(Color.FromArgb(72, 233, 229, 248), 0));
                glint.GradientStops.Add(new GradientStop(Color.FromArgb(28, 221, 218, 240), 0.45));
                glint.GradientStops.Add(new GradientStop(Color.FromArgb(0, 221, 218, 240), 1));
                glint.Freeze();
                dc.PushTransform(new RotateTransform(-37, 28, 20));
                dc.DrawEllipse(glint, null, new Point(28, 20), 5.6, 1.6);
                dc.Pop();
            }
            drawing.Freeze();
            return Rasterize(drawing);
        }

        private static BitmapSource Rasterize(Drawing drawing)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
            RenderTargetBitmap bitmap = new RenderTargetBitmap(352, 352, 384, 384, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static Brush CreateEyeLight()
        {
            RadialGradientBrush eye = new RadialGradientBrush();
            eye.GradientStops.Add(new GradientStop(Color.FromRgb(244, 94, 115), 0));
            eye.GradientStops.Add(new GradientStop(Color.FromRgb(231, 65, 92), 0.64));
            eye.GradientStops.Add(new GradientStop(Color.FromArgb(0, 202, 49, 79), 1));
            eye.Freeze();
            return eye;
        }

        private static Geometry CreateCheck()
        {
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(new Point(33, 44), false, false);
                g.LineTo(new Point(41, 52), true, false);
                g.LineTo(new Point(56, 36), true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static void DrawArc(DrawingContext dc, Point center, double radius, double ratio, Pen pen)
        {
            if (ratio <= 0) return;
            if (ratio >= 0.99999) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
            double angle = ratio * Math.PI * 2 - Math.PI / 2;
            StreamGeometry arc = new StreamGeometry();
            using (StreamGeometryContext g = arc.Open())
            {
                g.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                g.ArcTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
                    new Size(radius, radius), 0, ratio > 0.5, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, pen, arc);
        }

        private static Pen Stroke(Brush brush, double width)
        {
            Pen pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }
        private static Brush Solid(Color color) { SolidColorBrush brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        private static double Finite(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) ? 0 : value; }
        private static double Unit(double value) { return Math.Max(0, Math.Min(1, Finite(value))); }
        private static Color Blend(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
    }
}
