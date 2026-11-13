using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShengJi.UI
{
    /// <summary>
    /// 线条图标（16×16 坐标系，1.5px 线宽），颜色继承 Foreground。
    /// 用法：&lt;ui:Glyph Kind="mic" Width="16" Height="16"/&gt;
    /// </summary>
    public sealed class Glyph : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(Glyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Glyph), new FrameworkPropertyMetadata(Brushes.White,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(Glyph),
            new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

        public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

        public Glyph()
        {
            Width = 16; Height = 16;
            SnapsToDevicePixels = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Kind == null || !Shapes.TryGetValue(Kind, out var parts)) return;
            double s = Math.Min(ActualWidth, ActualHeight) / 16.0;
            if (s <= 0) return;
            double ox = (ActualWidth - 16 * s) / 2, oy = (ActualHeight - 16 * s) / 2;
            var pen = new Pen(Foreground, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            dc.PushTransform(new MatrixTransform(s, 0, 0, s, ox, oy));
            foreach (var p in parts)
            {
                if (p.Fill) dc.DrawGeometry(Foreground, null, p.Geometry);
                else dc.DrawGeometry(null, pen, p.Geometry);
            }
            dc.Pop();
        }

        // ------------------------------------------------------------------

        private struct Part { public Geometry Geometry; public bool Fill; }

        private static readonly Dictionary<string, Part[]> Shapes = new Dictionary<string, Part[]>
        {
            ["speaker"] = P("M2.5 6h2.3L8.5 3v10L4.8 10H2.5z", "M11 5.8a3 3 0 0 1 0 4.4", "M12.8 3.9a5.8 5.8 0 0 1 0 8.2"),
            ["vol"] = P("M2.5 6h2.3L8.5 3v10L4.8 10H2.5z", "M11 5.8a3 3 0 0 1 0 4.4"),
            ["mic"] = P(RRect(5.5, 1.5, 5, 8, 2.5), "M3 7.5a5 5 0 0 0 10 0", "M8 12.5v2"),
            ["layers"] = P("M8 2 1.8 5.3 8 8.6l6.2-3.3z", "M1.8 8.4 8 11.7l6.2-3.3", "M1.8 11.2 8 14.5l6.2-3.3"),
            ["app"] = P(RRect(1.8, 2.8, 12.4, 10.4, 1.6), "M1.8 5.8h12.4"),
            ["tune"] = P("M2 4.5h7M12.5 4.5H14M2 11.5h1.5M7 11.5h7", Circle(10.75, 4.5, 1.75), Circle(5.25, 11.5, 1.75)),
            ["pip"] = P(RRect(1.5, 2.5, 13, 11, 1.5), F(RRect(8, 8.5, 4.5, 3, 0.6))),
            ["min"] = P("M3.5 8h9"),
            ["close"] = P("M4 4l8 8M12 4l-8 8"),
            ["chevron"] = P("M4 6l4 4 4-4"),
            ["check"] = P("M3.5 8.5l3 3 6-7"),
            ["folder"] = P("M1.5 4.2c0-.6.4-1 1-1h3.3l1.6 1.6h6.1c.6 0 1 .4 1 1v6.5c0 .6-.4 1-1 1h-11c-.6 0-1-.4-1-1z"),
            ["pause"] = P(F(RRect(4, 3, 2.6, 10, 0.7)), F(RRect(9.4, 3, 2.6, 10, 0.7))),
            ["play"] = P(F("M5 3.3v9.4c0 .4.4.6.8.4l7.2-4.7c.3-.2.3-.6 0-.8L5.8 2.9c-.4-.2-.8 0-.8.4z")),
            ["stop"] = P(F(RRect(3.5, 3.5, 9, 9, 1.5))),
            ["expand"] = P("M9.5 2.5h4v4M6.5 13.5h-4v-4M13.5 2.5 9.2 6.8M2.5 13.5l4.3-4.3"),
            ["back"] = P("M10 3 5 8l5 5"),
            ["warn"] = P("M8 2 14.5 13.5h-13z", "M8 6.5v3.2M8 11.6v.1"),
            ["ok"] = P(Circle(8, 8, 6.5), "M5.2 8.2l2 2 3.6-4"),
            ["info"] = P(Circle(8, 8, 6.5), "M8 7.2v4M8 4.9v.1"),
            ["open"] = P("M9.5 2.5h4v4M13.5 2.5 7.5 8.5", "M12 9.5v3a1 1 0 0 1-1 1H3.5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h3"),
            ["keyboard"] = P(RRect(1.5, 4, 13, 8, 1.5), "M4.5 7h0M7 7h0M9.5 7h0M12 7h0M5 9.6h6"),
        };

        private static object F(string data) => new Part { Geometry = Parse(data), Fill = true };

        private static Part[] P(params object[] items)
        {
            var list = new List<Part>();
            foreach (var it in items)
            {
                if (it is Part p) list.Add(p);
                else list.Add(new Part { Geometry = Parse((string)it), Fill = false });
            }
            return list.ToArray();
        }

        private static Geometry Parse(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        private static string Circle(double cx, double cy, double r) =>
            string.Format(CultureInfo.InvariantCulture, "M{0} {1}a{2} {2} 0 1 0 {3} 0a{2} {2} 0 1 0 {4} 0z", cx - r, cy, r, 2 * r, -2 * r);

        private static string RRect(double x, double y, double w, double h, double r) =>
            string.Format(CultureInfo.InvariantCulture,
                "M{0} {1}H{2}A{4} {4} 0 0 1 {3} {5}V{6}A{4} {4} 0 0 1 {2} {7}H{0}A{4} {4} 0 0 1 {8} {6}V{5}A{4} {4} 0 0 1 {0} {1}z",
                x + r, y, x + w - r, x + w, r, y + r, y + h - r, y + h, x);
    }

    /// <summary>分段式电平表。Level / Peak 取值 0..1（对应 -60dB..0dB）。</summary>
    public sealed class LevelMeter : FrameworkElement
    {
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
            nameof(Peak), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SegmentProperty = DependencyProperty.Register(
            nameof(Segment), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(LevelMeter),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
        public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
        public double Segment { get => (double)GetValue(SegmentProperty); set => SetValue(SegmentProperty, value); }
        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

        private static readonly Brush Dim = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly Brush PeakBrush = Frozen(Color.FromArgb(0xD9, 0xF2, 0xF4, 0xF7));
        private static readonly Brush[] Ramp = BuildRamp();

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Brush[] BuildRamp()
        {
            var green = Color.FromRgb(0x2E, 0xCF, 0x86);
            var yellow = Color.FromRgb(0xE8, 0xC5, 0x47);
            var red = Color.FromRgb(0xFF, 0x5B, 0x5B);
            var r = new Brush[101];
            for (int i = 0; i <= 100; i++)
            {
                double x = i / 100.0;
                Color c;
                if (x <= 0.66) c = green;
                else if (x <= 0.80) c = Lerp(green, yellow, (x - 0.66) / 0.14);
                else if (x <= 0.94) c = Lerp(yellow, red, (x - 0.80) / 0.14);
                else c = red;
                r[i] = Frozen(c);
            }
            return r;
        }

        private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        public LevelMeter()
        {
            Height = 8;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            double seg = Segment, gap = Gap;
            if (w <= 0 || h <= 0) return;
            int count = Math.Max(1, (int)((w + gap) / (seg + gap)));
            double step = (w + gap) / count;
            double segW = step - gap;
            double level = Clamp01(Level), peak = Clamp01(Peak);
            int peakIdx = peak > 0.04 ? Math.Min(count - 1, (int)(peak * count)) : -1;
            double radius = Math.Min(1.0, segW / 2);

            for (int i = 0; i < count; i++)
            {
                double pos = (i + 0.5) / count;
                Brush b;
                if (i == peakIdx && pos > level) b = PeakBrush;
                else if (pos <= level) b = Ramp[(int)Math.Round(pos * 100)];
                else b = Dim;
                dc.DrawRoundedRectangle(b, null, new Rect(i * step, 0, segW, h), radius, radius);
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
