using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dictation.Services.Learning;

/// <summary>
/// A JSON number that remembers whether it is a Python <c>int</c> or
/// <c>float</c>, and — when it came off the wire — the exact token text, so a
/// line read back is re-emitted byte-for-byte (the contract the golden
/// fixtures pin: two encoders that agree on the values produce identical bytes).
/// </summary>
public readonly record struct Num
{
    public double Value { get; }
    public bool IsInteger { get; }
    public string? Raw { get; }

    private Num(double value, bool isInteger, string? raw)
    {
        Value = value;
        IsInteger = isInteger;
        Raw = raw;
    }

    public static Num Int(long v) => new(v, true, null);
    public static Num Float(double v) => new(v, false, null);
    public static Num FromRaw(string raw)
    {
        var isInt = raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0;
        return new(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture), isInt, raw);
    }

    public long AsLong => (long)Value;

    public string Text => Raw ?? (IsInteger ? ((long)Value).ToString(CultureInfo.InvariantCulture) : PyJson.FloatRepr(Value));
}

/// <summary>
/// The object graph the Python side serialises: <c>null</c>, <see cref="bool"/>,
/// <see cref="Num"/>, <see cref="string"/>, <c>List&lt;object?&gt;</c>,
/// <c>Dictionary&lt;string, object?&gt;</c> — plus writers that reproduce
/// <c>json.dumps(..., sort_keys=True, separators=(",", ":"), ensure_ascii=False)</c>
/// and <c>json.dumps(..., indent=2, ...)</c> byte-for-byte, and a parser that
/// keeps number tokens verbatim.
/// </summary>
public static class PyJson
{
    public static string DumpsCompact(object? value, bool sortKeys = true)
    {
        var sb = new StringBuilder();
        WriteCompact(sb, value, sortKeys);
        return sb.ToString();
    }

    public static string DumpsIndented(object? value, bool sortKeys, int indent = 2)
    {
        var sb = new StringBuilder();
        WriteIndented(sb, value, sortKeys, indent, 0);
        return sb.ToString();
    }

    private static void WriteCompact(StringBuilder sb, object? value, bool sortKeys)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case Num n:
                sb.Append(n.Text);
                break;
            case string s:
                WriteString(sb, s);
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(FloatRepr(d));
                break;
            case IDictionary<string, object?> dict:
            {
                sb.Append('{');
                var first = true;
                foreach (var kv in Ordered(dict, sortKeys))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteCompact(sb, kv.Value, sortKeys);
                }
                sb.Append('}');
                break;
            }
            case IEnumerable<object?> list:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    WriteCompact(sb, item, sortKeys);
                }
                sb.Append(']');
                break;
            }
            default:
                throw new InvalidOperationException($"PyJson cannot write {value.GetType()}");
        }
    }

    private static void WriteIndented(StringBuilder sb, object? value, bool sortKeys, int indent, int level)
    {
        switch (value)
        {
            case IDictionary<string, object?> dict:
            {
                if (dict.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                var first = true;
                foreach (var kv in Ordered(dict, sortKeys))
                {
                    sb.Append(first ? "\n" : ",\n");
                    first = false;
                    sb.Append(' ', indent * (level + 1));
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    WriteIndented(sb, kv.Value, sortKeys, indent, level + 1);
                }
                sb.Append('\n').Append(' ', indent * level).Append('}');
                return;
            }
            case string:
            case null:
            case bool:
            case Num:
            case int:
            case long:
            case double:
                WriteCompact(sb, value, sortKeys);
                return;
            case IEnumerable<object?> list:
            {
                var items = list.ToList();
                if (items.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                var first = true;
                foreach (var item in items)
                {
                    sb.Append(first ? "\n" : ",\n");
                    first = false;
                    sb.Append(' ', indent * (level + 1));
                    WriteIndented(sb, item, sortKeys, indent, level + 1);
                }
                sb.Append('\n').Append(' ', indent * level).Append(']');
                return;
            }
            default:
                throw new InvalidOperationException($"PyJson cannot write {value.GetType()}");
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> Ordered(IDictionary<string, object?> dict, bool sortKeys) =>
        sortKeys ? dict.OrderBy(kv => kv.Key, StringComparer.Ordinal) : dict;

    /// <summary>Python's encoder with <c>ensure_ascii=False</c>: only <c>"</c>, <c>\</c> and C0 controls are escaped.</summary>
    public static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Python <c>repr(float)</c>: shortest round-trip digits, a trailing
    /// <c>.0</c> on integral values, scientific notation only when the decimal
    /// exponent is below -4 or at least 16, two-digit signed exponents.</summary>
    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }
        if (double.IsPositiveInfinity(d))
        {
            return "Infinity";
        }
        if (double.IsNegativeInfinity(d))
        {
            return "-Infinity";
        }
        if (d == 0)
        {
            return double.IsNegative(d) ? "-0.0" : "0.0";
        }
        var r = d.ToString("R", CultureInfo.InvariantCulture);   // shortest round-trip on .NET Core 3.0+
        var negative = r.StartsWith('-');
        if (negative)
        {
            r = r[1..];
        }
        string digits;
        int exp; // decimal exponent of the first digit: value = 0.d1d2... * 10^(exp+1)
        var ePos = r.IndexOfAny(new[] { 'E', 'e' });
        string mantissa = ePos >= 0 ? r[..ePos] : r;
        var expPart = ePos >= 0 ? int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        digits = (intPart + fracPart).TrimStart('0');
        var leadingZeros = (intPart + fracPart).Length - digits.Length;
        // exponent of the first significant digit
        exp = intPart.Length - 1 - leadingZeros + expPart;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }
        if (exp < -4 || exp >= 16)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            }
            sb.Append('e').Append(exp < 0 ? '-' : '+');
            sb.Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        if (exp < 0)
        {
            sb.Append("0.").Append('0', -exp - 1).Append(digits);
            return sb.ToString();
        }
        if (digits.Length <= exp + 1)
        {
            sb.Append(digits).Append('0', exp + 1 - digits.Length).Append(".0");
            return sb.ToString();
        }
        sb.Append(digits, 0, exp + 1).Append('.').Append(digits, exp + 1, digits.Length - exp - 1);
        return sb.ToString();
    }

    // ── parsing ─────────────────────────────────────────────────────────

    public static object? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false });
        return Convert(doc.RootElement);
    }

    public static object? Convert(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return Num.FromRaw(el.GetRawText());
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray())
                {
                    list.Add(Convert(item));
                }
                return list;
            }
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject())
                {
                    dict[p.Name] = Convert(p.Value);
                }
                return dict;
            }
            default:
                throw new InvalidOperationException(el.ValueKind.ToString());
        }
    }

    // ── helpers for typed readers ───────────────────────────────────────

    public static Dictionary<string, object?> AsDict(object? v) =>
        v as Dictionary<string, object?> ?? throw new FormatException("expected a JSON object");

    public static string? AsStringOrNull(object? v) => v switch { null => null, string s => s, _ => throw new FormatException("expected a string") };

    public static string AsString(object? v) => AsStringOrNull(v) ?? throw new FormatException("expected a string");

    public static Num? AsNumOrNull(object? v) => v switch
    {
        null => null,
        Num n => n,
        long l => Num.Int(l),
        int i => Num.Int(i),
        double d => Num.Float(d),
        bool => throw new FormatException("expected a number, got a bool"),
        _ => throw new FormatException("expected a number"),
    };

    public static long AsLong(object? v) => (AsNumOrNull(v) ?? throw new FormatException("expected an integer")).AsLong;

    public static bool? AsBoolOrNull(object? v) => v switch { null => null, bool b => b, _ => throw new FormatException("expected a bool") };

    public static List<object?> AsList(object? v) => v as List<object?> ?? throw new FormatException("expected a JSON array");

    public static List<string> AsStringList(object? v) =>
        v is null ? new List<string>() : AsList(v).Select(x => AsString(x)).ToList();

    /// <summary>Deep copy of a parsed graph (lists and dicts are mutable).</summary>
    public static object? Clone(object? v) => v switch
    {
        Dictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Clone(kv.Value), StringComparer.Ordinal),
        List<object?> l => l.Select(Clone).ToList(),
        _ => v,
    };
}
