using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Avalonia.DirectWrite
{
    internal static class FontFamilyLoader
    {
        public static IEnumerable<Uri> LoadFontAssets(Uri source)
        {
            if (IsAvares(source) || IsAbsoluteResm(source))
            {
                return IsFontSource(source)
                    ? GetFontAssetsByExpression(source)
                    : GetFontAssetsBySource(source);
            }

            return Enumerable.Empty<Uri>();
        }

        public static bool IsFontSource(Uri uri)
        {
            var sourceWithoutArguments = GetSubString(uri.OriginalString, '?');
            return IsFontFile(sourceWithoutArguments);
        }

        public static bool IsFontFile(string filePath)
        {
            if (filePath is null)
                return false;

            return filePath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<Uri> GetFontAssetsBySource(Uri source)
        {
            var assetLoader = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();
            var availableAssets = assetLoader.GetAssets(source, null);
            return availableAssets.Where(IsFontSource);
        }

        private static IEnumerable<Uri> GetFontAssetsByExpression(Uri source)
        {
            var (fileNameWithoutExtension, extension) = GetFileName(source, out var location);
            var filePattern = CreateFilePattern(source, location, fileNameWithoutExtension);

            var assetLoader = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();
            var availableResources = assetLoader.GetAssets(location, null);

            return availableResources.Where(x => IsContainsFile(x, filePattern, extension));
        }

        private static (string fileNameWithoutExtension, string extension) GetFileName(Uri source, out Uri location)
        {
            if (IsAbsoluteResm(source))
            {
                var fileName = GetFileNameAndExtension(GetUnescapeAbsolutePath(source), '.');
                var uriLocation = GetUnescapeAbsoluteUri(source)
                    .Replace("." + fileName.fileNameWithoutExtension + fileName.extension, string.Empty);
                location = new Uri(uriLocation, UriKind.RelativeOrAbsolute);
                return fileName;
            }

            var filename = GetFileNameAndExtension(source.OriginalString);
            var fullFilename = filename.fileNameWithoutExtension + filename.extension;
            var uriString = GetUnescapeAbsoluteUri(source).Replace(fullFilename, string.Empty);
            location = new Uri(uriString);
            return filename;
        }

        private static string CreateFilePattern(Uri source, Uri location, string fileNameWithoutExtension)
        {
            var path = GetUnescapeAbsolutePath(location);
            var file = GetSubString(fileNameWithoutExtension, '*');
            return IsAbsoluteResm(source) ? path + "." + file : path + file;
        }

        private static bool IsContainsFile(Uri uri, string filePattern, string fileExtension)
        {
            var path = GetUnescapeAbsolutePath(uri);
            return path.IndexOf(filePattern, StringComparison.Ordinal) >= 0
                   && path.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAvares(Uri uri)
        {
            return uri.IsAbsoluteUri
                ? string.Equals(uri.Scheme, "avares", StringComparison.OrdinalIgnoreCase)
                : uri.OriginalString.StartsWith("avares:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAbsoluteResm(Uri uri)
        {
            return uri.IsAbsoluteUri
                ? string.Equals(uri.Scheme, "resm", StringComparison.OrdinalIgnoreCase)
                : uri.OriginalString.StartsWith("resm:", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetUnescapeAbsolutePath(Uri uri)
        {
            var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
            return Uri.UnescapeDataString(path);
        }

        private static string GetUnescapeAbsoluteUri(Uri uri)
        {
            var text = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
            return Uri.UnescapeDataString(text);
        }

        private static (string fileNameWithoutExtension, string extension) GetFileNameAndExtension(
            string path,
            char directorySeparator = '/')
        {
            var pathAsSpan = path.AsSpan();
            pathAsSpan = IsPathRooted(pathAsSpan, directorySeparator)
                ? pathAsSpan.Slice(1, path.Length - 1)
                : pathAsSpan;

            var extension = GetFileExtension(pathAsSpan);
            if (extension.Length == pathAsSpan.Length)
                return (extension.ToString(), string.Empty);

            var fileName = GetFileName(pathAsSpan, directorySeparator, extension.Length);
            return (fileName.ToString(), extension.ToString());
        }

        private static bool IsPathRooted(ReadOnlySpan<char> path, char directorySeparator) =>
            path.Length > 0 && path[0] == directorySeparator;

        private static ReadOnlySpan<char> GetFileExtension(ReadOnlySpan<char> path)
        {
            for (var i = path.Length - 1; i > 0; --i)
            {
                if (path[i] == '.')
                    return path.Slice(i, path.Length - i);
            }

            return path;
        }

        private static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path, char directorySeparator, int extensionLength)
        {
            for (var i = path.Length - extensionLength - 1; i >= 0; --i)
            {
                if (path[i] == directorySeparator)
                    return path.Slice(i + 1, path.Length - i - extensionLength - 1);
            }

            return path.Slice(0, path.Length - extensionLength);
        }

        private static string GetSubString(string path, char separator)
        {
            for (var i = 0; i < path.Length; i++)
            {
                if (path[i] == separator)
                    return path.Substring(0, i);
            }

            return path;
        }
    }
}
