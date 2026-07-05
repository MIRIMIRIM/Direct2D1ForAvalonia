using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Media;
using Avalonia.Platform;
using SharpGen.Runtime;
using Vortice.DirectWrite;
using FontFamily = Avalonia.Media.FontFamily;
using FontSimulations = Avalonia.Media.FontSimulations;
using FontStretch = Avalonia.Media.FontStretch;
using FontStyle = Avalonia.Media.FontStyle;
using FontWeight = Avalonia.Media.FontWeight;

namespace MIR.DirectWriteFontsForAvalonia
{
    [SupportedOSPlatform("windows")]
    internal class FontManagerImpl : IFontManagerImpl
    {
        private const string FallbackDefaultFamilyName = "Segoe UI";
        private static readonly object s_fallbackLock = new();
        private static IDWriteFontFallback? s_systemFontFallback;

        public string GetDefaultFontFamilyName()
        {
            return FallbackDefaultFamilyName;
        }

        public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false)
        {
            var familyCount = DirectWriteFontCollectionCache.InstalledFontCollection.FontFamilyCount;
            var fontFamilies = new string[familyCount];

            for (var i = 0; i < familyCount; i++)
            {
                fontFamilies[i] = DirectWriteFontCollectionCache.InstalledFontCollection.GetFontFamily((uint)i).FamilyNames.GetString(0);
            }

            return fontFamilies;
        }

        public bool TryMatchCharacter(
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(returnValue: true)] out IPlatformTypeface? platformTypeface)
        {
            if (!string.IsNullOrWhiteSpace(familyName)
                && TryCreateMatchingTypefaceForFamily(familyName, codepoint, fontStyle, fontWeight, fontStretch, out platformTypeface))
            {
                return true;
            }

            if (TryMatchCharacterWithSystemFallback(
                    codepoint,
                    fontStyle,
                    fontWeight,
                    fontStretch,
                    familyName,
                    culture,
                    out platformTypeface))
            {
                return true;
            }

            var familyCount = DirectWriteFontCollectionCache.InstalledFontCollection.FontFamilyCount;

            for (var i = 0u; i < familyCount; i++)
            {
                using var family = DirectWriteFontCollectionCache.InstalledFontCollection.GetFontFamily(i);
                using var fontSet = family.GetMatchingFonts(
                    (Vortice.DirectWrite.FontWeight)fontWeight,
                    (Vortice.DirectWrite.FontStretch)fontStretch,
                    (Vortice.DirectWrite.FontStyle)fontStyle);

                for (var fontIndex = 0u; fontIndex < fontSet.FontCount; fontIndex++)
                {
                    var font = fontSet.GetFont(fontIndex);
                    if (!font.HasCharacter((uint)codepoint))
                    {
                        font.Dispose();
                        continue;
                    }

                    platformTypeface = new DirectWriteGlyphTypeface(font);
                    return true;
                }
            }

            platformTypeface = null;
            return false;
        }

        private static bool TryMatchCharacterWithSystemFallback(
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(returnValue: true)] out IPlatformTypeface? platformTypeface)
        {
            platformTypeface = null;

            if (!TryGetSystemFontFallback(out var fontFallback))
                return false;

            string text;
            try
            {
                text = char.ConvertFromUtf32(codepoint);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            var locale = culture?.Name;
            if (string.IsNullOrWhiteSpace(locale))
                locale = CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrWhiteSpace(locale))
                locale = "en-US";

            var baseFamilyName = NormalizeFamilyName(familyName);
            if (baseFamilyName is not null
                && !DirectWriteFontCollectionCache.InstalledFontCollection.FindFamilyName(baseFamilyName, out _))
            {
                baseFamilyName = null;
            }

            try
            {
                using var source = new FallbackTextAnalysisSource(text, locale);
                fontFallback.MapCharacters(
                    source,
                    textPosition: 0,
                    textLength: (uint)text.Length,
                    baseFontCollection: DirectWriteFontCollectionCache.InstalledFontCollection,
                    baseFamilyName: baseFamilyName,
                    baseWeight: (Vortice.DirectWrite.FontWeight)fontWeight,
                    baseStyle: (Vortice.DirectWrite.FontStyle)fontStyle,
                    baseStretch: (Vortice.DirectWrite.FontStretch)fontStretch,
                    mappedLength: 0,
                    mappedFont: out var mappedFont,
                    scale: out _);

                if (mappedFont is null)
                    return false;

                if (!mappedFont.HasCharacter((uint)codepoint))
                {
                    mappedFont.Dispose();
                    return false;
                }

                platformTypeface = new DirectWriteGlyphTypeface(mappedFont);
                return true;
            }
            catch
            {
                platformTypeface = null;
                return false;
            }
        }

        private static bool TryGetSystemFontFallback([NotNullWhen(true)] out IDWriteFontFallback? fontFallback)
        {
            if (s_systemFontFallback is not null)
            {
                fontFallback = s_systemFontFallback;
                return true;
            }

            lock (s_fallbackLock)
            {
                if (s_systemFontFallback is not null)
                {
                    fontFallback = s_systemFontFallback;
                    return true;
                }

                try
                {
                    using var factory2 = DirectWritePlatform.DirectWriteFactory.QueryInterface<IDWriteFactory2>();
                    s_systemFontFallback = factory2.SystemFontFallback;
                    fontFallback = s_systemFontFallback;
                    return fontFallback is not null;
                }
                catch
                {
                    fontFallback = null;
                    return false;
                }
            }
        }

        private static string? NormalizeFamilyName(string? familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return null;

            return familyName == FontFamily.DefaultFontFamilyName
                ? FallbackDefaultFamilyName
                : familyName;
        }

        public bool TryCreateGlyphTypeface(
            string familyName,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            [NotNullWhen(returnValue: true)] out IPlatformTypeface? platformTypeface)
        {
            var systemFonts = DirectWriteFontCollectionCache.InstalledFontCollection;

            if (familyName == FontFamily.DefaultFontFamilyName)
            {
                familyName = FallbackDefaultFamilyName;
            }

            if (systemFonts.FindFamilyName(familyName, out var index))
            {
                var font = systemFonts.GetFontFamily(index).GetFirstMatchingFont(
                    (Vortice.DirectWrite.FontWeight)weight,
                    (Vortice.DirectWrite.FontStretch)stretch,
                    (Vortice.DirectWrite.FontStyle)style);

                platformTypeface = new DirectWriteGlyphTypeface(font, familyNameOverride: familyName);
                return true;
            }

            platformTypeface = null;
            return false;
        }

        public bool TryCreateGlyphTypeface(
            Stream stream,
            FontSimulations fontSimulations,
            [NotNullWhen(returnValue: true)] out IPlatformTypeface? platformTypeface)
        {
            byte[] fontData;
            if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out var buffer))
            {
                fontData = buffer.AsSpan(0, (int)memoryStream.Length).ToArray();
            }
            else
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                fontData = copy.ToArray();
            }

            using var fontStream = new MemoryStream(fontData, writable: false);
            var fontLoader = new DirectWriteResourceFontLoader(DirectWritePlatform.DirectWriteFactory, [fontStream]);
            var fontCollection = DirectWritePlatform.DirectWriteFactory.CreateCustomFontCollection(
                fontLoader,
                fontLoader.Key.PositionPointer,
                (uint)fontLoader.Key.RemainingLength);

            if (fontCollection.FontFamilyCount > 0)
            {
                var fontFamily = fontCollection.GetFontFamily(0);

                if (fontFamily.FontCount > 0)
                {
                    var font = fontFamily.GetFont(0);
                    platformTypeface = new DirectWriteGlyphTypeface(font, fontSimulations, fontData);
                    return true;
                }
            }

            platformTypeface = null;
            return false;
        }

        public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = null;

            var systemFonts = DirectWriteFontCollectionCache.InstalledFontCollection;
            if (familyName == FontFamily.DefaultFontFamilyName)
            {
                familyName = FallbackDefaultFamilyName;
            }

            if (!systemFonts.FindFamilyName(familyName, out var index))
            {
                return false;
            }

            using var family = systemFonts.GetFontFamily(index);
            var result = new List<Typeface>((int)family.FontCount);
            var seen = new HashSet<(FontStyle Style, FontWeight Weight, FontStretch Stretch)>();

            for (var i = 0u; i < family.FontCount; i++)
            {
                using var font = family.GetFont(i);
                var key = (
                    Style: (FontStyle)font.Style,
                    Weight: (FontWeight)font.Weight,
                    Stretch: (FontStretch)font.Stretch);
                if (!seen.Add(key))
                    continue;

                result.Add(new Typeface(familyName, key.Style, key.Weight, key.Stretch));
            }

            familyTypefaces = result;
            return result.Count != 0;
        }

        private static bool TryCreateMatchingTypefaceForFamily(
            string familyName,
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            [NotNullWhen(returnValue: true)] out IPlatformTypeface? platformTypeface)
        {
            var systemFonts = DirectWriteFontCollectionCache.InstalledFontCollection;
            if (familyName == FontFamily.DefaultFontFamilyName)
            {
                familyName = FallbackDefaultFamilyName;
            }

            if (!systemFonts.FindFamilyName(familyName, out var familyIndex))
            {
                platformTypeface = null;
                return false;
            }

            using var family = systemFonts.GetFontFamily(familyIndex);
            using var fontSet = family.GetMatchingFonts(
                (Vortice.DirectWrite.FontWeight)fontWeight,
                (Vortice.DirectWrite.FontStretch)fontStretch,
                (Vortice.DirectWrite.FontStyle)fontStyle);

            for (var fontIndex = 0u; fontIndex < fontSet.FontCount; fontIndex++)
            {
                var font = fontSet.GetFont(fontIndex);
                if (!font.HasCharacter((uint)codepoint))
                {
                    font.Dispose();
                    continue;
                }

                platformTypeface = new DirectWriteGlyphTypeface(font, familyNameOverride: familyName);
                return true;
            }

            platformTypeface = null;
            return false;
        }

        private sealed unsafe class FallbackTextAnalysisSource : CallbackBase, IDWriteTextAnalysisSource
        {
            private readonly string _text;
            private readonly string _locale;
            private readonly GCHandle _textHandle;
            private readonly GCHandle _localeHandle;

            public FallbackTextAnalysisSource(string text, string locale)
            {
                _text = text;
                _locale = locale;
                _textHandle = GCHandle.Alloc(_text, GCHandleType.Pinned);
                _localeHandle = GCHandle.Alloc(_locale, GCHandleType.Pinned);
            }

            uint IDWriteTextAnalysisSource.GetTextAtPosition(uint textPosition, nint textString)
            {
                if (textPosition >= _text.Length)
                {
                    Marshal.WriteIntPtr(textString, IntPtr.Zero);
                    return 0;
                }

                var textPtr = (char*)_textHandle.AddrOfPinnedObject();
                Marshal.WriteIntPtr(textString, (IntPtr)(textPtr + textPosition));

                return (uint)(_text.Length - textPosition);
            }

            uint IDWriteTextAnalysisSource.GetTextBeforePosition(uint textPosition, nint textString)
            {
                if (textPosition == 0 || _text.Length == 0)
                {
                    Marshal.WriteIntPtr(textString, IntPtr.Zero);
                    return 0;
                }

                Marshal.WriteIntPtr(textString, _textHandle.AddrOfPinnedObject());

                return Math.Min(textPosition, (uint)_text.Length);
            }

            ReadingDirection IDWriteTextAnalysisSource.GetParagraphReadingDirection() => ReadingDirection.LeftToRight;

            uint IDWriteTextAnalysisSource.GetLocaleName(uint textPosition, nint localeName)
            {
                if (textPosition >= _text.Length)
                {
                    Marshal.WriteIntPtr(localeName, IntPtr.Zero);
                    return 0;
                }

                Marshal.WriteIntPtr(localeName, _localeHandle.AddrOfPinnedObject());

                return (uint)(_text.Length - textPosition);
            }

            void IDWriteTextAnalysisSource.GetNumberSubstitution(uint textPosition, out uint textLength, out IDWriteNumberSubstitution numberSubstitution)
            {
                textLength = textPosition >= _text.Length ? 0 : (uint)(_text.Length - textPosition);
                numberSubstitution = default!;
            }

            protected override void DisposeCore(bool disposing)
            {
                if (_textHandle.IsAllocated)
                    _textHandle.Free();

                if (_localeHandle.IsAllocated)
                    _localeHandle.Free();

                base.DisposeCore(disposing);
            }
        }
    }
}
