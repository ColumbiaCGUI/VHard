using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public sealed class HoldAffordanceCatalog
{
    private readonly Dictionary<string, int> minFingersByScanId;

    private HoldAffordanceCatalog(Dictionary<string, int> minFingersByScanId)
    {
        this.minFingersByScanId = minFingersByScanId;
    }

    public int Count => minFingersByScanId.Count;
    public IEnumerable<string> ScanIds => minFingersByScanId.Keys;

    public int ResolveMinFingers(string scanId, int defaultMinFingers)
    {
        GripEngagementGate.ValidateMinFingers(defaultMinFingers);
        return !string.IsNullOrEmpty(scanId) && minFingersByScanId.TryGetValue(scanId, out int value)
            ? value
            : defaultMinFingers;
    }

    public static bool TryParse(string json, out HoldAffordanceCatalog catalog, out string error)
    {
        catalog = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Hold affordance sidecar is empty.";
            return false;
        }

        try
        {
            int index = 0;
            Dictionary<string, int> values = new(StringComparer.OrdinalIgnoreCase);
            SkipWhitespace(json, ref index);
            Expect(json, ref index, '{');
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
            {
                SkipWhitespace(json, ref index);
                EnsureEnd(json, index);
                catalog = new HoldAffordanceCatalog(values);
                return true;
            }

            while (true)
            {
                string scanId = ParseString(json, ref index);
                if (string.IsNullOrWhiteSpace(scanId))
                {
                    throw new FormatException("Scan IDs cannot be empty.");
                }
                SkipWhitespace(json, ref index);
                Expect(json, ref index, ':');
                SkipWhitespace(json, ref index);
                int minFingers = ParseInteger(json, ref index);
                if (minFingers != 1 && minFingers != 2)
                {
                    throw new FormatException("Pocket override for " + scanId + " must be 1 or 2.");
                }
                if (!values.TryAdd(scanId, minFingers))
                {
                    throw new FormatException("Duplicate scan ID: " + scanId + ".");
                }

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                {
                    break;
                }
                Expect(json, ref index, ',');
                SkipWhitespace(json, ref index);
            }

            SkipWhitespace(json, ref index);
            EnsureEnd(json, index);
            catalog = new HoldAffordanceCatalog(values);
            return true;
        }
        catch (Exception exception) when (exception is FormatException || exception is OverflowException)
        {
            error = "Hold affordance JSON is invalid: " + exception.Message;
            return false;
        }
    }

    private static string ParseString(string json, ref int index)
    {
        Expect(json, ref index, '"');
        StringBuilder value = new();
        while (index < json.Length)
        {
            char character = json[index++];
            if (character == '"')
            {
                return value.ToString();
            }
            if (character != '\\')
            {
                if (character < 0x20)
                {
                    throw new FormatException("Control character in scan ID.");
                }
                value.Append(character);
                continue;
            }
            if (index >= json.Length)
            {
                throw new FormatException("Incomplete escape sequence.");
            }
            char escaped = json[index++];
            switch (escaped)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (index + 4 > json.Length ||
                        !ushort.TryParse(json.Substring(index, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ushort codePoint))
                    {
                        throw new FormatException("Invalid Unicode escape sequence.");
                    }
                    value.Append((char)codePoint);
                    index += 4;
                    break;
                default:
                    throw new FormatException("Unsupported escape sequence.");
            }
        }
        throw new FormatException("Unterminated scan ID.");
    }

    private static int ParseInteger(string json, ref int index)
    {
        int start = index;
        if (index < json.Length && json[index] == '-')
        {
            index++;
        }
        int digits = index;
        while (index < json.Length && char.IsDigit(json[index]))
        {
            index++;
        }
        if (digits == index)
        {
            throw new FormatException("Expected an integer pocket override.");
        }
        return int.Parse(json.Substring(start, index - start), CultureInfo.InvariantCulture);
    }

    private static void SkipWhitespace(string json, ref int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index]))
        {
            index++;
        }
    }

    private static void Expect(string json, ref int index, char expected)
    {
        if (!TryConsume(json, ref index, expected))
        {
            throw new FormatException("Expected '" + expected + "' at character " + index + ".");
        }
    }

    private static bool TryConsume(string json, ref int index, char expected)
    {
        if (index >= json.Length || json[index] != expected)
        {
            return false;
        }
        index++;
        return true;
    }

    private static void EnsureEnd(string json, int index)
    {
        if (index != json.Length)
        {
            throw new FormatException("Unexpected content after the JSON object.");
        }
    }
}
