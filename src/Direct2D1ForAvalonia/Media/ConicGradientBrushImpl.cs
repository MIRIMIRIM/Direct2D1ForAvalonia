using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Vortice.Direct2D1;
using Vortice.DXGI;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal sealed class ConicGradientBrushImpl : BrushImpl
    {
        private const int MaxBitmapDimension = 2048;
        private readonly ID2D1Bitmap1? _bitmap;

        public ConicGradientBrushImpl(
            IConicGradientBrush brush,
            ID2D1DeviceContext target,
            Rect destinationRect)
        {
            if (brush.GradientStops.Count == 0 ||
                destinationRect.Width <= 0 ||
                destinationRect.Height <= 0)
            {
                return;
            }

            var dpi = target.Dpi;
            var scale = Math.Min(1.0, MaxBitmapDimension / Math.Max(destinationRect.Width, destinationRect.Height));
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(destinationRect.Width * dpi.Width / 96.0 * scale));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(destinationRect.Height * dpi.Height / 96.0 * scale));
            var pixels = CreatePixels(brush, destinationRect, pixelWidth, pixelHeight);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

            try
            {
                _bitmap = target.CreateBitmap(
                    new Vortice.Mathematics.SizeI(pixelWidth, pixelHeight),
                    handle.AddrOfPinnedObject(),
                    (uint)(pixelWidth * 4),
                    new BitmapProperties1(
                        new Vortice.DCommon.PixelFormat
                        {
                            AlphaMode = Vortice.DCommon.AlphaMode.Premultiplied,
                            Format = Format.B8G8R8A8_UNorm
                        },
                        dpi.Width,
                        dpi.Height,
                        BitmapOptions.None));

                var bitmapDipWidth = pixelWidth * 96.0 / dpi.Width;
                var bitmapDipHeight = pixelHeight * 96.0 / dpi.Height;
                var transform = Matrix3x2.CreateScale(
                        (float)(destinationRect.Width / bitmapDipWidth),
                        (float)(destinationRect.Height / bitmapDipHeight))
                    * Matrix3x2.CreateTranslation(
                        (float)destinationRect.X,
                        (float)destinationRect.Y);

                PlatformBrush = target.CreateBitmapBrush(
                    _bitmap,
                    new BitmapBrushProperties1
                    {
                        ExtendModeX = ExtendMode.Clamp,
                        ExtendModeY = ExtendMode.Clamp,
                        InterpolationMode = InterpolationMode.Linear
                    },
                    new BrushProperties
                    {
                        Opacity = 1,
                        Transform = transform
                    });
            }
            finally
            {
                handle.Free();
            }
        }

        public override void Dispose()
        {
            _bitmap?.Dispose();
            base.Dispose();
        }

        private static byte[] CreatePixels(
            IConicGradientBrush brush,
            Rect destinationRect,
            int pixelWidth,
            int pixelHeight)
        {
            var stops = brush.GradientStops
                .OrderBy(x => x.Offset)
                .Select(x => new GradientStopData(x.Color, x.Offset))
                .ToArray();
            var pixels = new byte[checked(pixelWidth * pixelHeight * 4)];
            var center = brush.Center.ToPixels(destinationRect);
            var angleOffset = (brush.Angle - 90.0) * Math.PI / 180.0;
            var opacity = Math.Clamp(brush.Opacity, 0, 1);

            for (var y = 0; y < pixelHeight; y++)
            {
                var py = destinationRect.Y + ((y + 0.5) * destinationRect.Height / pixelHeight);
                for (var x = 0; x < pixelWidth; x++)
                {
                    var px = destinationRect.X + ((x + 0.5) * destinationRect.Width / pixelWidth);
                    var t = (Math.Atan2(py - center.Y, px - center.X) - angleOffset) / (Math.PI * 2.0);
                    t = ApplySpread(t, brush.SpreadMethod);

                    var color = Interpolate(stops, t);
                    var a = color.A / 255.0 * opacity;
                    var index = ((y * pixelWidth) + x) * 4;

                    pixels[index] = Premultiply(color.B, a);
                    pixels[index + 1] = Premultiply(color.G, a);
                    pixels[index + 2] = Premultiply(color.R, a);
                    pixels[index + 3] = ToByte(a * 255.0);
                }
            }

            return pixels;
        }

        private static double ApplySpread(double value, GradientSpreadMethod spreadMethod)
        {
            return spreadMethod switch
            {
                GradientSpreadMethod.Reflect => Reflect(value),
                GradientSpreadMethod.Repeat => value - Math.Floor(value),
                _ => Math.Clamp(value, 0, 1)
            };
        }

        private static double Reflect(double value)
        {
            value -= Math.Floor(value / 2.0) * 2.0;
            return value > 1.0 ? 2.0 - value : value;
        }

        private static Color Interpolate(GradientStopData[] stops, double offset)
        {
            if (stops.Length == 1 || offset <= stops[0].Offset)
                return stops[0].Color;

            for (var i = 1; i < stops.Length; i++)
            {
                if (offset <= stops[i].Offset)
                {
                    var previous = stops[i - 1];
                    var next = stops[i];
                    var range = next.Offset - previous.Offset;
                    var t = range > 0 ? (offset - previous.Offset) / range : 0;
                    return Color.FromArgb(
                        ToByte(Lerp(previous.Color.A, next.Color.A, t)),
                        ToByte(Lerp(previous.Color.R, next.Color.R, t)),
                        ToByte(Lerp(previous.Color.G, next.Color.G, t)),
                        ToByte(Lerp(previous.Color.B, next.Color.B, t)));
                }
            }

            return stops[^1].Color;
        }

        private static double Lerp(byte from, byte to, double progress)
            => from + ((to - from) * progress);

        private static byte Premultiply(byte value, double alpha)
            => ToByte(value * alpha);

        private static byte ToByte(double value)
            => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

        private readonly struct GradientStopData
        {
            public GradientStopData(Color color, double offset)
            {
                Color = color;
                Offset = offset;
            }

            public Color Color { get; }
            public double Offset { get; }
        }
    }
}
