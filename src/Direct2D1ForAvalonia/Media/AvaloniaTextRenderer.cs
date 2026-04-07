using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace MIR.Direct2D1ForAvalonia.Media
{
    internal class AvaloniaTextRenderer(
        DrawingContextImpl context,
        ID2D1RenderTarget target,
        ID2D1Brush foreground) : TextRendererBase
    {
        private readonly DrawingContextImpl _context = context;

        private readonly ID2D1RenderTarget _renderTarget = target;

        private readonly ID2D1Brush _foreground = foreground;

        public override void DrawGlyphRun(
            nint clientDrawingContext,
            float baselineOriginX,
            float baselineOriginY,
            MeasuringMode measuringMode,
            GlyphRun glyphRun,
            GlyphRunDescription glyphRunDescription,
            IUnknown clientDrawingEffect)
        {
            var comObject = (ComObject)clientDrawingEffect;
            var wrapper = (BrushWrapper)Marshal.GetObjectForIUnknown(comObject.NativePointer);

            // TODO: Work out how to get the size below rather than passing new Size().
            ID2D1Brush brush = _foreground;
            var shouldDisposeBrush = false;
            if (wrapper != null)
            {
                var createdBrush = _context.CreateBrush(wrapper.Brush, default).PlatformBrush;
                if (createdBrush != null)
                {
                    brush = createdBrush;
                    shouldDisposeBrush = true;
                }
            }

            _renderTarget.DrawGlyphRun(
                new Vector2 { X = baselineOriginX, Y = baselineOriginY },
                glyphRun,
                brush,
                measuringMode);

            if (shouldDisposeBrush)
            {
                brush.Dispose();
            }
        }

        public override Matrix3x2 GetCurrentTransform(nint clientDrawingContext)
        {
            return _renderTarget.Transform;
        }

        public override float GetPixelsPerDip(nint clientDrawingContext)
        {
            return _renderTarget.Dpi.Width / 96;
        }
    }
}