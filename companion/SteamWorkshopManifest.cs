using System.Text;

namespace CodexWallpaperSkin;

internal static class SteamWorkshopManifest
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private const int MaximumTokens = 200_000;

    public static IReadOnlySet<string> ReadDownloadedSubscriptions(string manifestPath)
    {
        var file = new FileInfo(manifestPath);
        if (!file.Exists || file.Length <= 0 || file.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The Steam Wallpaper Engine workshop manifest is missing or has an invalid size.");
        }
        return ParseDownloadedSubscriptions(File.ReadAllText(manifestPath));
    }

    internal static IReadOnlySet<string> ParseDownloadedSubscriptions(string text)
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var tokens = Tokenize(text).Take(MaximumTokens + 1).ToArray();
        if (tokens.Length > MaximumTokens)
        {
            throw new InvalidDataException("The Steam workshop manifest contains too many tokens.");
        }

        var sawInstalled = false;
        var sawDetails = false;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token == "}")
            {
                if (stack.Count == 0) throw new InvalidDataException("The Steam workshop manifest has unbalanced braces.");
                stack.Pop();
                continue;
            }
            if (token == "{") throw new InvalidDataException("The Steam workshop manifest contains an unnamed object.");
            if (index + 1 >= tokens.Length) break;

            var next = tokens[index + 1];
            if (next != "{")
            {
                index++;
                continue;
            }

            if (stack.Count > 0 && stack.Peek().Equals("WorkshopItemsInstalled", StringComparison.Ordinal))
            {
                if (IsWorkshopId(token)) installed.Add(token);
            }
            else if (stack.Count > 0 && stack.Peek().Equals("WorkshopItemDetails", StringComparison.Ordinal))
            {
                if (IsWorkshopId(token)) subscribed.Add(token);
            }
            if (token.Equals("WorkshopItemsInstalled", StringComparison.Ordinal)) sawInstalled = true;
            if (token.Equals("WorkshopItemDetails", StringComparison.Ordinal)) sawDetails = true;
            stack.Push(token);
            index++;
        }
        if (stack.Count != 0 || !sawInstalled || !sawDetails)
        {
            throw new InvalidDataException("The Steam workshop manifest is incomplete or malformed.");
        }

        installed.IntersectWith(subscribed);
        return installed;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        for (var index = 0; index < text.Length;)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }
            if (character is '{' or '}')
            {
                yield return character.ToString();
                index++;
                continue;
            }
            if (character == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index += 2;
                while (index < text.Length && text[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (character != '"')
            {
                throw new InvalidDataException("The Steam workshop manifest contains an unexpected token.");
            }

            index++;
            var value = new StringBuilder();
            var closed = false;
            while (index < text.Length)
            {
                character = text[index++];
                if (character == '"')
                {
                    closed = true;
                    break;
                }
                if (character == '\\' && index < text.Length)
                {
                    var escaped = text[index++];
                    if (escaped is '"' or '\\') value.Append(escaped);
                    else value.Append('\\').Append(escaped);
                }
                else
                {
                    value.Append(character);
                }
            }
            if (!closed) throw new InvalidDataException("The Steam workshop manifest contains an unterminated string.");
            yield return value.ToString();
        }
    }

    private static bool IsWorkshopId(string value) =>
        value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);
}
