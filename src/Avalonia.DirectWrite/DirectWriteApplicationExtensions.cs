using System.Runtime.Versioning;

namespace Avalonia
{
    [SupportedOSPlatform("windows")]
    public static class DirectWriteApplicationExtensions
    {
        public static AppBuilder UseDirectWrite(this AppBuilder builder)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DirectWrite text shaping is only supported on Windows.");

            return builder.UseTextShapingSubsystem(Avalonia.DirectWrite.DirectWritePlatform.Initialize, "DirectWrite");
        }
    }
}
