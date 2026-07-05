using Avalonia;
using Avalonia.Platform;
using SharpGen.Runtime;
using Vortice.DirectWrite;

namespace MIR.DirectWriteFontsForAvalonia
{
    internal static class DirectWritePlatform
    {
        private static readonly object s_initLock = new();
        private static bool s_initialized;

        internal static IDWriteFactory1 DirectWriteFactory { get; private set; } = null!;

        internal static void InitializeDirectWrite()
        {
            SharpGenRuntimeInitializer.Initialize();

            if (s_initialized)
                return;

            lock (s_initLock)
            {
                if (s_initialized)
                    return;

                DirectWriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory1>();
                s_initialized = true;
            }
        }

        internal static void InitializeFontManager()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MIR.Direct2D1ForAvalonia DirectWrite font manager is only supported on Windows.");

            InitializeDirectWrite();
            AvaloniaLocator.CurrentMutable.Bind<IFontManagerImpl>().ToConstant(new FontManagerImpl());
        }

    }

    internal static class SharpGenRuntimeInitializer
    {
        private static bool s_configured;
        private static readonly object s_lock = new();

        public static void Initialize()
        {
            if (s_configured)
                return;

            lock (s_lock)
            {
                if (s_configured)
                    return;

                try
                {
                    Configuration.EnableReleaseOnFinalizer = true;
                }
                catch (SharpGenException)
                {
                    // Another subsystem may have already frozen the configuration.
                }

                s_configured = true;
            }
        }
    }
}
