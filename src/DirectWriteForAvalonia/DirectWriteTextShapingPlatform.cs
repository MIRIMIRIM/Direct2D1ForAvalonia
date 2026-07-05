using Avalonia;
using Avalonia.Platform;
using MIR.DirectWriteFontsForAvalonia;

namespace MIR.DirectWriteForAvalonia
{
    internal static class DirectWriteTextShapingPlatform
    {
        internal static void Initialize()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MIR.DirectWriteForAvalonia text shaping is only supported on Windows.");

            DirectWritePlatform.InitializeFontManager();
            AvaloniaLocator.CurrentMutable
                .Bind<ITextShaperImpl>().ToConstant(new DirectWriteTextShaper());
        }
    }
}
