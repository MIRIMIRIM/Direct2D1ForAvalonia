using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using Avalonia.Media;
using Avalonia.Platform;
using FontFamily = Avalonia.Media.FontFamily;
using FontStretch = Avalonia.Media.FontStretch;
using FontStyle = Avalonia.Media.FontStyle;
using FontWeight = Avalonia.Media.FontWeight;

namespace MIR.DirectWriteForAvalonia
{
    [SupportedOSPlatform("windows")]
    internal class FontManagerImpl : IFontManagerImpl
    {
        private const string FallbackDefaultFamilyName = "Segoe UI";

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
    }
}