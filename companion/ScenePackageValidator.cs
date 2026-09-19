using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexWallpaperSkin;

public sealed record ScenePackageInfo(string Version, int EntryCount, long DataStart);

public static partial class ScenePackageValidator
{
    public const long MaximumPackageBytes = 128L * 1024 * 1024;
    public const int MaximumEntries = 4_096;
    private const int MaximumEntryNameBytes = 1_024;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;
    private const long MaximumSceneJsonBytes = 4L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryValidate(string? path, out ScenePackageInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            using var stream = OpenValidated(path);
            info = Validate(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static FileStream OpenValidated(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Wallpaper Engine scene.pkg was not found.", fullPath);
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse-point scene packages are not accepted.");
        }
        if (file.Length <= 0 || file.Length > MaximumPackageBytes)
        {
            throw new InvalidDataException($"scene.pkg must be between 1 byte and {MaximumPackageBytes / (1024 * 1024)} MiB.");
        }

        var stream = new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan
        });
        try
        {
            _ = Validate(stream);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static ScenePackageInfo Validate(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length <= 0 || stream.Length > MaximumPackageBytes)
        {
            throw new InvalidDataException("The scene package stream is unavailable or exceeds the size limit.");
        }

        stream.Position = 0;
        var magicLength = checked((int)ReadUInt32(stream));
        if (magicLength != 8)
        {
            throw new InvalidDataException("scene.pkg has an invalid version header length.");
        }
        var magic = StrictUtf8.GetString(ReadExact(stream, magicLength));
        if (!PackageVersionPattern().IsMatch(magic))
        {
            throw new InvalidDataException("scene.pkg has an unsupported PKGV version header.");
        }
        var numericVersion = int.Parse(magic.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture);
        if (numericVersion is < 12 or > 23)
        {
            throw new InvalidDataException("scene.pkg version is outside the supported PKGV0012-PKGV0023 range.");
        }

        var countValue = ReadUInt32(stream);
        if (countValue == 0 || countValue > MaximumEntries)
        {
            throw new InvalidDataException($"scene.pkg must contain between 1 and {MaximumEntries:N0} entries.");
        }
        var count = checked((int)countValue);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Name, long Offset, long Size)>(count);
        for (var index = 0; index < count; index++)
        {
            var nameLengthValue = ReadUInt32(stream);
            if (nameLengthValue == 0 || nameLengthValue > MaximumEntryNameBytes)
            {
                throw new InvalidDataException($"scene.pkg entry {index} has an invalid name length.");
            }
            var name = StrictUtf8.GetString(ReadExact(stream, checked((int)nameLengthValue)));
            ValidateEntryName(name);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"scene.pkg contains a duplicate entry name: {name}");
            }
            var offset = (long)ReadUInt32(stream);
            var size = (long)ReadUInt32(stream);
            if (size <= 0 || size > MaximumEntryBytes)
            {
                throw new InvalidDataException($"scene.pkg entry '{name}' exceeds the per-entry size limit.");
            }
            entries.Add((name, offset, size));
        }

        var dataStart = stream.Position;
        var ranges = new List<(long Start, long End, string Name)>(count);
        foreach (var entry in entries)
        {
            var start = checked(dataStart + entry.Offset);
            var end = checked(start + entry.Size);
            if (start < dataStart || end < start || end > stream.Length)
            {
                throw new InvalidDataException($"scene.pkg entry '{entry.Name}' points outside the package.");
            }
            ranges.Add((start, end, entry.Name));
        }
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Start < ranges[index - 1].End)
            {
                throw new InvalidDataException($"scene.pkg entries '{ranges[index - 1].Name}' and '{ranges[index].Name}' overlap.");
            }
        }

        var sceneEntry = entries.FirstOrDefault(entry => entry.Name.Equals("scene.json", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(sceneEntry.Name) || sceneEntry.Size <= 0 || sceneEntry.Size > MaximumSceneJsonBytes)
        {
            throw new InvalidDataException("scene.pkg has no bounded scene.json entry.");
        }
        var sceneStart = checked(dataStart + sceneEntry.Offset);
        stream.Position = sceneStart;
        using (var sceneDocument = JsonDocument.Parse(ReadExact(stream, checked((int)sceneEntry.Size)), new JsonDocumentOptions
        {
            MaxDepth = 64,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        }))
        {
            if (sceneDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("scene.pkg scene.json must contain a JSON object.");
            }
        }
        stream.Position = 0;
        return new ScenePackageInfo(magic, count, dataStart);
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExact(stream, bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        var bytes = new byte[count];
        ReadExact(stream, bytes);
        return bytes;
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException("scene.pkg ended before its declared metadata or payload.");
            }
            offset += read;
        }
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith('/')
            || name.Contains('\\')
            || name.Contains('\0')
            || name.Contains(':')
            || name.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException("scene.pkg contains an unsafe entry path.");
        }
    }

    [GeneratedRegex("^PKGV[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionPattern();
}
