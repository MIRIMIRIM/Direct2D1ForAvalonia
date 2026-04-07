using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using DWrite = Vortice.DirectWrite;

namespace Avalonia.DirectWrite
{
    internal sealed class DirectWriteGlyphTypeface : IPlatformTypeface
    {
        private readonly byte[]? _streamData;
        private readonly string? _localPath;
        private bool _isDisposed;

        public DirectWriteGlyphTypeface(
            DWrite.IDWriteFont font,
            FontSimulations fontSimulations = FontSimulations.None,
            byte[]? streamData = null,
            string? familyNameOverride = null)
        {
            DWFont = font ?? throw new ArgumentNullException(nameof(font));
            FontFace = DWFont.CreateFontFace().QueryInterface<DWrite.IDWriteFontFace1>();

            _streamData = streamData;
            _localPath = streamData is null ? TryResolveLocalPath(font) : null;

            FamilyName = familyNameOverride ?? DWFont.FontFamily.FamilyNames.GetString(0);
            Weight = (FontWeight)DWFont.Weight;
            Style = (FontStyle)DWFont.Style;
            Stretch = (FontStretch)DWFont.Stretch;
            FontSimulations = fontSimulations;
        }

        public DWrite.IDWriteFont DWFont { get; }
        public DWrite.IDWriteFontFace1 FontFace { get; }
        public string FamilyName { get; }
        public FontWeight Weight { get; }
        public FontStyle Style { get; }
        public FontStretch Stretch { get; }
        public FontSimulations FontSimulations { get; }

        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            ThrowIfDisposed();

            table = default;

            var dwTag = SwapBytes((uint)tag);
            if (!FontFace.TryGetFontTable(dwTag, out var tableData, out var tableContext))
                return false;

            try
            {
                table = tableData.ToArray();
                return !table.IsEmpty;
            }
            finally
            {
                FontFace.ReleaseFontTable(tableContext);
            }
        }

        public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
        {
            ThrowIfDisposed();

            if (_streamData is not null)
            {
                stream = new MemoryStream(_streamData, writable: false);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_localPath) && File.Exists(_localPath))
            {
                stream = File.OpenRead(_localPath);
                return true;
            }

            stream = null;
            return false;
        }

        private static uint SwapBytes(uint x)
        {
            x = (x >> 16) | (x << 16);
            return ((x & 0xFF00FF00) >> 8) | ((x & 0x00FF00FF) << 8);
        }

        private static unsafe string? TryResolveLocalPath(DWrite.IDWriteFont font)
        {
            using var face = font.CreateFontFace();
            var files = face.GetFiles();
            if (files.Length == 0)
                return null;

            using var file = files[0];
            if (file.Loader is not DWrite.IDWriteLocalFontFileLoader localLoader)
                return null;

            var key = file.GetReferenceKey();
            if (key.Length == 0)
                return null;

            fixed (byte* keyPtr = key)
            {
                var pathLength = localLoader.GetFilePathLengthFromKey((nint)keyPtr, (uint)key.Length);
                if (pathLength == 0)
                    return null;

                var chars = new char[pathLength + 1];
                fixed (char* pathPtr = chars)
                {
                    localLoader.GetFilePathFromKey((nint)keyPtr, (uint)key.Length, (nint)pathPtr, pathLength + 1);
                }

                return new string(chars, 0, (int)pathLength);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(DirectWriteGlyphTypeface));
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            FontFace.Dispose();
            DWFont.Dispose();
        }
    }
}
