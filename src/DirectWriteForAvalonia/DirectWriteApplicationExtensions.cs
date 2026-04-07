using System.Runtime.Versioning;
using Avalonia;

namespace MIR.DirectWriteForAvalonia
{
    [SupportedOSPlatform("windows")]
    public static class DirectWriteApplicationExtensions
    {
        public static AppBuilder UseDirectWrite(this AppBuilder builder)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MIR.DirectWriteForAvalonia text shaping is only supported on Windows.");

            return builder.UseTextShapingSubsystem(DirectWritePlatform.Initialize, "DirectWrite");
        }
    }
}
