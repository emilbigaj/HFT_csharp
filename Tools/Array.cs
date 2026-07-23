//BEGIN_FILE HFT/Tools/Array.cs
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tools;

internal static class ArraySerializer
{
    public static void Write<T>(Utf8JsonWriter writer, ReadOnlySpan<T> values, JsonSerializerOptions options) where T : struct
    {
        writer.WriteStartArray();
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

        for (int i = 0; i < values.Length; i++)
        {
            JsonSerializer.Serialize(writer, values[i], typeInfo);
        }

        writer.WriteEndArray();
    }

    public static void Read<T>(ref Utf8JsonReader reader, scoped Span<T> destination, JsonSerializerOptions options) where T : struct
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected start of array.");

        int index = 0;
        int capacity = destination.Length;
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (index >= capacity)
                throw new IndexOutOfRangeException($"JSON array length exceeds fixed capacity of {capacity}.");

            destination[index] = JsonSerializer.Deserialize<T>(ref reader, typeInfo)!;
            index++;
        }
    }
}

// --- Inline Arrays ---

[JsonConverter(typeof(InlineArrayConverterFactory))]
[InlineArray(4)]
public struct Array4<T> where T : struct
{
    private T _element0;
}

[JsonConverter(typeof(InlineArrayConverterFactory))]
[InlineArray(8)]
public struct Array8<T> where T : struct
{
    private T _element0;
}

[JsonConverter(typeof(InlineArrayConverterFactory))]
[InlineArray(16)]
public struct Array16<T> where T : struct
{
    private T _element0;
}

[JsonConverter(typeof(InlineArrayConverterFactory))]
[InlineArray(32)]
public struct Array32<T> where T : struct
{
    private T _element0;
}

[JsonConverter(typeof(InlineArrayConverterFactory))]
[InlineArray(64)]
public struct Array64<T> where T : struct
{
    private T _element0;
}

// --- Factory ---

public sealed class InlineArrayConverterFactory : JsonConverterFactory
{
    private static readonly Dictionary<(Type, Type), Func<JsonConverter>> s_registry = new Dictionary<(Type, Type), Func<JsonConverter>>();

    static InlineArrayConverterFactory()
    {
        Register<int>();
        Register<long>();
        Register<double>();
        Register<float>();
        Register<uint>();
        Register<ulong>();
        Register<short>();
        Register<ushort>();
        Register<byte>();
        Register<sbyte>();

    }

    public static void Register<T>() where T : struct
    {
        s_registry[(typeof(Array4<>), typeof(T))] = () => new Array4Converter<T>();
        s_registry[(typeof(Array8<>), typeof(T))] = () => new Array8Converter<T>();
        s_registry[(typeof(Array16<>), typeof(T))] = () => new Array16Converter<T>();
        s_registry[(typeof(Array32<>), typeof(T))] = () => new Array32Converter<T>();
        s_registry[(typeof(Array64<>), typeof(T))] = () => new Array64Converter<T>();
    }

    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType)
            return false;

        Type genericDef = typeToConvert.GetGenericTypeDefinition();

        return genericDef == typeof(Array4<>) || genericDef == typeof(Array8<>) || genericDef == typeof(Array16<>) || genericDef == typeof(Array32<>) || genericDef == typeof(Array64<>);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        Type genericDef = typeToConvert.GetGenericTypeDefinition();
        Type elementType = typeToConvert.GetGenericArguments()[0];
        (Type, Type) key = (genericDef, elementType);

        if (s_registry.TryGetValue(key, out Func<JsonConverter>? factory))
            return factory();

        throw new NotSupportedException($"Type {elementType.Name} is not registered in InlineArrayConverterFactory. Call InlineArrayConverterFactory.Register<T>() at startup.");
    }
}

// --- Dedicated Converters ---

public sealed class Array4Converter<T> : JsonConverter<Array4<T>> where T : struct
{
    public override Array4<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Array4<T> result = default;
        ArraySerializer.Read(ref reader, MemoryMarshal.CreateSpan(ref Unsafe.As<Array4<T>, T>(ref result), 4), options);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Array4<T> value, JsonSerializerOptions options)
    {
        ArraySerializer.Write(writer, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Array4<T>, T>(ref Unsafe.AsRef(in value)), 4), options);
    }
}

public sealed class Array8Converter<T> : JsonConverter<Array8<T>> where T : struct
{
    public override Array8<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Array8<T> result = default;
        ArraySerializer.Read(ref reader, MemoryMarshal.CreateSpan(ref Unsafe.As<Array8<T>, T>(ref result), 8), options);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Array8<T> value, JsonSerializerOptions options)
    {
        ArraySerializer.Write(writer, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Array8<T>, T>(ref Unsafe.AsRef(in value)), 8), options);
    }
}

public sealed class Array16Converter<T> : JsonConverter<Array16<T>> where T : struct
{
    public override Array16<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Array16<T> result = default;
        ArraySerializer.Read(ref reader, MemoryMarshal.CreateSpan(ref Unsafe.As<Array16<T>, T>(ref result), 16), options);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Array16<T> value, JsonSerializerOptions options)
    {
        ArraySerializer.Write(writer, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Array16<T>, T>(ref Unsafe.AsRef(in value)), 16), options);
    }
}

public sealed class Array32Converter<T> : JsonConverter<Array32<T>> where T : struct
{
    public override Array32<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Array32<T> result = default;
        ArraySerializer.Read(ref reader, MemoryMarshal.CreateSpan(ref Unsafe.As<Array32<T>, T>(ref result), 32), options);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Array32<T> value, JsonSerializerOptions options)
    {
        ArraySerializer.Write(writer, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Array32<T>, T>(ref Unsafe.AsRef(in value)), 32), options);
    }
}

public sealed class Array64Converter<T> : JsonConverter<Array64<T>> where T : struct
{
    public override Array64<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Array64<T> result = default;
        ArraySerializer.Read(ref reader, MemoryMarshal.CreateSpan(ref Unsafe.As<Array64<T>, T>(ref result), 64), options);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Array64<T> value, JsonSerializerOptions options)
    {
        ArraySerializer.Write(writer, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Array64<T>, T>(ref Unsafe.AsRef(in value)), 64), options);
    }
}