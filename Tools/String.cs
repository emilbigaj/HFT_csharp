//BEGIN_FILE HFT/Tools/String.cs
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tools;

internal static unsafe class FixedString
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(byte* destination, string? s, int capacity)
    {
        if (s != null && s.Length > capacity)
            throw new ArgumentException($"FixedString<{capacity}>.Set can not fit {s}");

        int len = s is null ? 0 : s.Length;
        int i = 0;

        for (; i < len; i++)
            destination[i] = (byte)s![i];

        for (; i < capacity; i++)
            destination[i] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LengthUntilZero(byte* p, int capacity)
    {
        int i = 0;

        while (i < capacity && p[i] != 0)
            i++;

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string ToString(byte* p, int capacity)
    {
        int len = LengthUntilZero(p, capacity);

        return Encoding.ASCII.GetString(p, len);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Equals(byte* a, byte* b, int capacity)
    {
        int i = 0;

        for (; i <= capacity - 8; i += 8)
        {
            if (*(ulong*)(a + i) != *(ulong*)(b + i))
                return false;
        }

        if (i <= capacity - 4)
        {
            if (*(uint*)(a + i) != *(uint*)(b + i))
                return false;
            i += 4;
        }

        for (; i < capacity; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetHashCode(byte* p, int capacity, bool stopAtZero = true)
    {
        const uint Offset = 2166136261u;
        const uint Prime = 16777619u;
        uint h = Offset;

        if (stopAtZero)
        {
            for (int i = 0; i < capacity && p[i] != 0; i++)
            {
                h ^= p[i];
                h *= Prime;
            }
        }
        else
        {
            for (int i = 0; i < capacity; i++)
            {
                h ^= p[i];
                h *= Prime;
            }
        }

        return (int)h;
    }
}

// ============================= String4 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String4JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
public unsafe struct String4 : IEquatable<String4>
{
    private const int s_capacity = 4;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String4(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String4(string? s) => new String4(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String4 left, String4 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String4 left, String4 right) => !(left == right);

    public bool Equals(String4 other) => this == other;

    public override bool Equals(object? obj) => obj is String4 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================= String8 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String8JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
public unsafe struct String8 : IEquatable<String8>
{
    private const int s_capacity = 8;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String8(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String8(string? s) => new String8(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String8 left, String8 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String8 left, String8 right) => !(left == right);

    public bool Equals(String8 other) => this == other;

    public override bool Equals(object? obj) => obj is String8 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ String16 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String16JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public unsafe struct String16 : IEquatable<String16>
{
    private const int s_capacity = 16;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String16(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String16(string? s) => new String16(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String16 left, String16 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String16 left, String16 right) => !(left == right);

    public bool Equals(String16 other) => this == other;

    public override bool Equals(object? obj) => obj is String16 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ String32 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String32JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
public unsafe struct String32 : IEquatable<String32>
{
    private const int s_capacity = 32;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String32(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String32(string? s) => new String32(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String32 left, String32 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String32 left, String32 right) => !(left == right);

    public bool Equals(String32 other) => this == other;

    public override bool Equals(object? obj) => obj is String32 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ String64 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String64JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public unsafe struct String64 : IEquatable<String64>
{
    private const int s_capacity = 64;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String64(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String64(string? s) => new String64(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String64 left, String64 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String64 left, String64 right) => !(left == right);

    public bool Equals(String64 other) => this == other;

    public override bool Equals(object? obj) => obj is String64 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ String128 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String128JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 128)]
public unsafe struct String128 : IEquatable<String128>
{
    private const int s_capacity = 128;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String128(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String128(string? s) => new String128(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String128 left, String128 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String128 left, String128 right) => !(left == right);

    public bool Equals(String128 other) => this == other;

    public override bool Equals(object? obj) => obj is String128 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ String256 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String256JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 256)]
public unsafe struct String256 : IEquatable<String256>
{
    private const int s_capacity = 256;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String256(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String256(string? s) => new String256(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String256 left, String256 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String256 left, String256 right) => !(left == right);

    public bool Equals(String256 other) => this == other;

    public override bool Equals(object? obj) => obj is String256 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ String512 =============================
[DebuggerDisplay("{AsString}")]
[JsonConverter(typeof(String512JsonConverter))]
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 512)]
public unsafe struct String512 : IEquatable<String512>
{
    private const int s_capacity = 512;
    public int Capacity => s_capacity;

    private fixed byte _value[s_capacity];
    public string AsString => ToString();

    public String512(string? value)
    {
        Set(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator String512(string? s) => new String512(s);

    public void Set(string? s)
    {
        fixed (byte* p = _value)
        {
            FixedString.Set(p, s, s_capacity);
        }
    }

    public override string ToString()
    {
        fixed (byte* p = _value)
        {
            return FixedString.ToString(p, s_capacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(String512 left, String512 right)
    {
        byte* a = (byte*)Unsafe.AsPointer(ref left);
        byte* b = (byte*)Unsafe.AsPointer(ref right);

        return FixedString.Equals(a, b, s_capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(String512 left, String512 right) => !(left == right);

    public bool Equals(String512 other) => this == other;

    public override bool Equals(object? obj) => obj is String512 o && this == o;

    public override int GetHashCode()
    {
        fixed (byte* p = _value)
        {
            return FixedString.GetHashCode(p, s_capacity, stopAtZero: true);
        }
    }
}

// ============================ STJ Converters =============================

public sealed class String4JsonConverter : JsonConverter<String4>
{
    public override String4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String4(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String4 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String8JsonConverter : JsonConverter<String8>
{
    public override String8 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String8(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String8 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String16JsonConverter : JsonConverter<String16>
{
    public override String16 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String16(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String16 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String32JsonConverter : JsonConverter<String32>
{
    public override String32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String32(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String32 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String64JsonConverter : JsonConverter<String64>
{
    public override String64 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String64(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String64 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String128JsonConverter : JsonConverter<String128>
{
    public override String128 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String128(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String128 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String256JsonConverter : JsonConverter<String256>
{
    public override String256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String256(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String256 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class String512JsonConverter : JsonConverter<String512>
{
    public override String512 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new String512(reader.GetString() ?? string.Empty);
    public override void Write(Utf8JsonWriter writer, String512 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
//END_FILE HFT/Tools/String.cs