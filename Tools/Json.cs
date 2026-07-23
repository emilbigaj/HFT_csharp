//BEGIN_FILE HFT/Tools/Json.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tools;

// Restrict to types only. Inherited = false ensures we don't accidentally register derived classes blindly.
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Enum, Inherited = false)]
public sealed class RegisterJsonAttribute : Attribute
{
}

[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
public sealed partial class SystemJsonContext : JsonSerializerContext
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        Json.RegisterContext(Default);
    }
}

public static class Json
{
    /// <summary>
    /// A proxy resolver that holds a mutable list of contexts. 
    /// This bypasses the JsonSerializerOptions read-only lock caused by lazy assembly loading.
    /// </summary>
    private sealed class MutableResolver : IJsonTypeInfoResolver
    {
        private readonly List<IJsonTypeInfoResolver> _resolvers = new();
        private readonly object _lock = new object();

        public void Register(IJsonTypeInfoResolver resolver)
        {
            lock (_lock)
            {
                if (!_resolvers.Contains(resolver))
                {
                    _resolvers.Add(resolver);
                }
            }
        }

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            lock (_lock)
            {
                foreach (var resolver in _resolvers)
                {
                    var info = resolver.GetTypeInfo(type, options);
                    if (info != null)
                    {
                        return info;
                    }
                }
            }
            return null;
        }
    }

    private static readonly MutableResolver s_resolver = new();

    public static readonly JsonSerializerOptions Options;
    public static readonly JsonSerializerOptions OptionsToLine;

    static Json()
    {
        Options = new JsonSerializerOptions
        {
            // Field/property naming
            PropertyNamingPolicy = null,           // preserves C# names (PascalCase)
            DictionaryKeyPolicy = null,            // also preserve dictionary keys

            PropertyNameCaseInsensitive = true,   // tolerate "Open"/"open" on read
            IncludeFields = true,

            // Output format
            WriteIndented = true,                // flat (no pretty-print)

            // Include/exclude behavior
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // omit nulls on write

            // Numbers & enums
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.Strict,
            AllowTrailingCommas = true,

            ReadCommentHandling = JsonCommentHandling.Skip, // accept // comments and /* ... */

            // Assign our proxy resolver permanently
            TypeInfoResolver = s_resolver
        };

        // Add standard converters
        Options.Converters.Add(new TimeZoneInfoJsonConverter()); // TimeZoneInfo ↔ Id
        Options.Converters.Add(new TimeSpanJsonConverter());     // TimeSpan ↔ "HH:mm:ss"
        Options.Converters.Add(new TimestampJsonConverter());    // Timestamp ↔ "yyyy-MM-dd HH:mm:ss.fff_fff_fff"
        Options.Converters.Add(new DurationJsonConverter());     // Duration ↔ string
        Options.Converters.Add(new DoubleJsonConverter());
        Options.Converters.Add(new FloatJsonConverter());
        Options.Converters.Add(new Bitset64JsonConverter());
        Options.Converters.Add(new ExceptionJsonConverterFactory());

        // The MutableResolver chain only contains source-gen contexts; GetTypeInfo
        // doesn't consult Options.Converters on its own (that's a DefaultJsonTypeInfoResolver
        // behavior we deliberately don't include — it'd enable reflection fallback for any
        // unregistered type in AOT). Register a tiny resolver that synthesizes a
        // JsonTypeInfo<Exception> from the converter so GetTypeInfo(typeof(Exception)) works.
        s_resolver.Register(new ExceptionTypeInfoResolver());

        OptionsToLine = new JsonSerializerOptions(Options)
        {
            WriteIndented = false,
        };
    }

    private sealed class ExceptionTypeInfoResolver : IJsonTypeInfoResolver
    {
        private static readonly ExceptionJsonConverter s_converter = new ExceptionJsonConverter();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (!typeof(Exception).IsAssignableFrom(type)) return null;
            return JsonMetadataServices.CreateValueInfo<Exception>(options, s_converter);
        }
    }

    /// <summary>
    /// Allows downstream assemblies to inject their Native AOT contexts at startup via [ModuleInitializer].
    /// </summary>
    public static void RegisterContext(IJsonTypeInfoResolver context)
    {
        s_resolver.Register(context);
    }

    // =========================================================================
    // FLEXIBLE API (Cold/Warm Paths - Uses global options & resolver chain)
    // =========================================================================

    public static string Serialize(object obj, JsonSerializerOptions options)
    {
        // Exception subclasses aren't in the AOT source-gen context; resolve as the
        // base Exception type so the registered ExceptionJsonConverterFactory handles it.
        Type type = obj is Exception ? typeof(Exception) : obj.GetType();
        JsonTypeInfo typeInfo = options.GetTypeInfo(type);
        return JsonSerializer.Serialize(obj, typeInfo);
    }

    public static string Serialize<T>(T obj, JsonSerializerOptions options)
    {
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        return JsonSerializer.Serialize(obj, typeInfo);
    }

    public static void Serialize<T>(Utf8JsonWriter writer, T obj, JsonSerializerOptions options)
    {
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        JsonSerializer.Serialize(writer, obj, typeInfo);
    }

    public static string Serialize(object obj)
    {
        return Serialize(obj, Options);
    }

    public static string SerializeToLine(object obj)
    {
        return Serialize(obj, OptionsToLine);
    }

    public static string Serialize<T>(T obj)
    {
        return Serialize(obj, Options);
    }

    public static string SerializeToLine<T>(T obj)
    {
        return Serialize(obj, OptionsToLine);
    }

    public static T Deserialize<T>(string json)
    {
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)OptionsToLine.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize<T>(json, typeInfo)!;
    }

    public static T Deserialize<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize<T>(ref reader, typeInfo)!;
    }
}

public sealed class TimeZoneInfoJsonConverter : JsonConverter<TimeZoneInfo>
{
    public override TimeZoneInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? id = reader.GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new JsonException("TimeZoneInfo id is null/empty.");
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    public override void Write(Utf8JsonWriter writer, TimeZoneInfo value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Id);
    }
}

public sealed class Bitset64JsonConverter : JsonConverter<Bitset64>
{
    public override Bitset64 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ulong raw = reader.GetUInt64();
        return new Bitset64(raw);
    }

    public override void Write(Utf8JsonWriter writer, Bitset64 value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Raw);
    }
}

public sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    private const string Format = @"hh\:mm\:ss";

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new JsonException("TimeSpan string is null/empty.");
        return TimeSpan.ParseExact(text, Format, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
    }
}

public sealed class DurationJsonConverter : JsonConverter<Duration>
{
    public override Duration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("Duration string is null or empty.");
        }

        return Duration.FromString(text);
    }

    public override void Write(Utf8JsonWriter writer, Duration value, JsonSerializerOptions options)
    {
        string text = value.ToString();
        writer.WriteStringValue(text);
    }
}

public sealed class TimestampJsonConverter : JsonConverter<Timestamp>
{
    public override Timestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("Timestamp string is null or empty.");
        }

        return Timestamp.FromString(text);
    }

    public override void Write(Utf8JsonWriter writer, Timestamp value, JsonSerializerOptions options)
    {
        string text = value.ToString();
        writer.WriteStringValue(text);
    }
}

/// <summary>
/// Converts double to plain JSON number format (e.g., 123.21312)
/// without exponential notation ("1E-07").
/// </summary>
public sealed class DoubleJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Fast path: It's a raw JSON number (e.g., 123.45)
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDouble();
        }

        // Handshake path: It's a JSON string (e.g., "NaN", "Infinity", or "123.45")
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();

            if (string.IsNullOrWhiteSpace(text)) return 0.0;

            // double.Parse natively handles "NaN" and "Infinity" with InvariantCulture
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing double.");
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value))
        {
            // Keep your specific formatting for finite numbers
            string formatted = value.ToString("0.#############################", CultureInfo.InvariantCulture);
            writer.WriteRawValue(formatted, skipInputValidation: true);
        }
        else
        {
            // Manually write as a string to avoid the ArgumentException
            // This produces "NaN", "Infinity", or "-Infinity"
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// Converts float to plain JSON number format (e.g., 123.21312)
/// without exponential notation ("1E-07").
/// </summary>
public sealed class FloatJsonConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Fast path: It's a raw JSON number
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetSingle();
        }

        // Handshake path: Handles "NaN", "Infinity", or quoted numbers from Glaze
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                return 0.0f;
            }

            return float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing float.");
    }

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (float.IsFinite(value))
        {
            // Keep your specific formatting for finite numbers
            string formatted = value.ToString("0.#############################", CultureInfo.InvariantCulture);
            writer.WriteRawValue(formatted, skipInputValidation: true);
        }
        else
        {
            // Manually write as a string to avoid the ArgumentException
            // This produces "NaN", "Infinity", or "-Infinity"
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }
}

public sealed class ExceptionJsonConverterFactory : JsonConverterFactory
{
    // Cache the instance to avoid allocations
    private static readonly ExceptionJsonConverter s_converter = new ExceptionJsonConverter();

    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(Exception).IsAssignableFrom(typeToConvert);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return s_converter;
    }
}

public sealed class ExceptionJsonConverter : JsonConverter<Exception>
{
    public override Exception Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // For logging purposes, we typically do not need to deserialize exceptions back into executable objects.
        // We skip the JSON token to ensure the reader advances.
        using (JsonDocument document = JsonDocument.ParseValue(ref reader))
        {
            string message = document.RootElement.TryGetProperty("Message", out JsonElement messageElement)
                ? messageElement.GetString() ?? "Unknown Exception"
                : "Unknown Exception";

            return new Exception($"Deserialized Log Exception: {message}");
        }
    }

    public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("Type", value.GetType().FullName);
        writer.WriteString("Message", value.Message);

        writer.WriteString("StackTrace", value.StackTrace ?? "");
        writer.WriteString("Source", value.Source);

        if (value.InnerException != null)
        {
            writer.WritePropertyName("InnerException");
            // Recursively write the inner exception using this same converter
            Write(writer, value.InnerException, options);
        }

        writer.WriteEndObject();
    }
}

public static class JsonExtenstions
{
    public static bool GetBoolean(this JsonElement jsonElement, string propertyName)
    {
        return jsonElement.GetProperty(propertyName).GetBoolean();
    }

    public static string GetString(this JsonElement jsonElement, string propertyName)
    {
        return jsonElement.GetProperty(propertyName).GetString() ?? string.Empty;
    }

    public static double GetDouble(this JsonElement jsonElement, string propertyName)
    {
        jsonElement = jsonElement.GetProperty(propertyName);

        if (jsonElement.ValueKind == JsonValueKind.String)
        {
            return double.Parse(jsonElement.GetString() ?? "0",
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture);
        }

        return jsonElement.GetDouble();
    }

    public static decimal GetDecimal(this JsonElement jsonElement, string propertyName)
    {
        jsonElement = jsonElement.GetProperty(propertyName);

        if (jsonElement.ValueKind == JsonValueKind.String)
        {
            return decimal.Parse(jsonElement.GetString() ?? "0",
                NumberStyles.Number,
                CultureInfo.InvariantCulture);
        }

        return jsonElement.GetDecimal();
    }

    public static long GetLong(this JsonElement jsonElement, string propertyName)
    {
        jsonElement = jsonElement.GetProperty(propertyName);

        if (jsonElement.ValueKind == JsonValueKind.String)
        {
            return long.Parse(jsonElement.GetString() ?? "0",
                NumberStyles.Integer,
                CultureInfo.InvariantCulture);
        }

        return jsonElement.GetInt64();
    }

    public static int GetInt(this JsonElement jsonElement, string propertyName)
    {
        jsonElement = jsonElement.GetProperty(propertyName);

        if (jsonElement.ValueKind == JsonValueKind.String)
        {
            return int.Parse(jsonElement.GetString() ?? "0",
                NumberStyles.Integer,
                CultureInfo.InvariantCulture);
        }

        return jsonElement.GetInt32();
    }

    public static bool TryGetBoolean(this JsonElement json, string propertyName, out bool value)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty(propertyName, out JsonElement property) &&
            (property.ValueKind == JsonValueKind.False || property.ValueKind == JsonValueKind.True))
        {
            value = property.GetBoolean();
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryGetString(this JsonElement json, string propertyName, out string value)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetDouble(this JsonElement json, string propertyName, out double value)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty(propertyName, out JsonElement property))
        {
            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.TryGetDouble(out value);
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var s = property.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return double.TryParse(s,
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out value);
                }
            }
        }

        value = default;
        return false;
    }

    public static bool TryGetLong(this JsonElement json, string propertyName, out long value)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty(propertyName, out JsonElement property))
        {
            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.TryGetInt64(out value);
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var s = property.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return long.TryParse(s,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out value);
                }
            }
        }

        value = default;
        return false;
    }

    public static bool TryGetInt(this JsonElement json, string propertyName, out int value)
    {
        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty(propertyName, out JsonElement property))
        {
            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.TryGetInt32(out value);
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var s = property.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return int.TryParse(s,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out value);
                }
            }
        }

        value = default;
        return false;
    }
}
//END_FILE HFT/Tools/Json.cs