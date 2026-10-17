using EATDF.Heat;
using EATDF.Types;
using EATDF.Visitors;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EATDF.Heat2;

internal class Heat2Writer(Heat2Encoder encoder, Stream stream)
{
    const int MaxStackAlloc = 512;

    static readonly Encoding StringEncoder = Encoding.UTF8;

    /// <summary>
    /// 3 bytes for tag, 1 byte for type
    /// </summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Whether heat1 back compatibility mode is enabled.
    /// </summary>
    private bool Heat1BackCompatibility => Encoder.Heat1BackCompatibility;

    /// <summary>
    /// The encoder that created this writer
    /// </summary>
    public Heat2Encoder Encoder { get; } = encoder;

    /// <summary>
    /// The stream to write to
    /// </summary>
    public Stream Stream { get; } = stream;

    /// <summary>
    /// Writes the specified type value to the stream.
    /// </summary>
    /// <typeparam name="T">The type of the value to write.</typeparam>
    /// <param name="writer">The writer which performs the write operation.</param>
    /// <param name="value">The value to write.</param>
    delegate void TypeWriter<T>(Heat2Writer writer, T value, bool visitHeader);
    delegate void TypeWriter(Heat2Writer writer, object? value, bool visitHeader);

    /// <summary>
    /// Initial type writers for basic types.
    /// </summary>
    static readonly ConcurrentDictionary<Type, Delegate> _typeWriters = new ConcurrentDictionary<Type, Delegate>()
    {
        [typeof(bool)] = new TypeWriter<bool>((writer, value, _) => writer.WriteBool(value)),
        [typeof(sbyte)] = new TypeWriter<sbyte>((writer, value, _) => writer.WriteInt8(value)),
        [typeof(byte)] = new TypeWriter<byte>((writer, value, _) => writer.WriteUInt8(value)),
        [typeof(short)] = new TypeWriter<short>((writer, value, _) => writer.WriteInt16(value)),
        [typeof(ushort)] = new TypeWriter<ushort>((writer, value, _) => writer.WriteUInt16(value)),
        [typeof(int)] = new TypeWriter<int>((writer, value, _) => writer.WriteInt32(value)),
        [typeof(uint)] = new TypeWriter<uint>((writer, value, _) => writer.WriteUInt32(value)),
        [typeof(long)] = new TypeWriter<long>((writer, value, _) => writer.WriteInt64(value)),
        [typeof(ulong)] = new TypeWriter<ulong>((writer, value, _) => writer.WriteUInt64(value)),
        [typeof(string)] = new TypeWriter<string>((writer, value, _) => writer.WriteString(value)),
        [typeof(byte[])] = new TypeWriter<byte[]>((writer, value, _) => writer.WriteBlob(value)),
        [typeof(ObjectType)] = new TypeWriter<ObjectType>((writer, value, _) => writer.WriteObjectType(value)),
        [typeof(ObjectId)] = new TypeWriter<ObjectId>((writer, value, _) => writer.WriteObjectId(value)),

        //// for lists and maps of unknown type arguments
        //[typeof(IList)] = new TypeWriter<IList>(WriteUnknownList),
        //[typeof(IDictionary)] = new TypeWriter<IDictionary>(WriteUnknownMap),
    };

    static MethodInfo _createListWriterMethod = typeof(Heat2Writer).GetMethod(nameof(createListTypeWriter), BindingFlags.NonPublic | BindingFlags.Static)!;
    static MethodInfo _createMapWriterMethod = typeof(Heat2Writer).GetMethod(nameof(createMapTypeWriter), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Returns the writer for the specified type.
    /// </summary>
    /// <typeparam name="T">The type to get the writer for.</typeparam>
    /// <returns>The writer for the specified type.</returns>
    static TypeWriter<T> GetTypeWriter<T>()
    {
        Type valueType = typeof(T);
        return (TypeWriter<T>)_typeWriters.GetOrAdd(valueType, CreateCustomWriter<T>);
    }

    static TypeWriter<T> CreateCustomWriter<T>(Type type)
    {
        if (type.IsSubclassOf(typeof(Union)))
            return (Heat2Writer writer, T value, bool visitHeader) =>
            {
                if (value is Union union)
                {
                    writer.Stream.WriteByte(union.ActiveIndex);
                    union.Visit(writer.Encoder, union, visitHeader);
                    //writer.Encoder.VisitTdf(union);
                }
            };

        if (type.IsSubclassOf(typeof(Tdf)))
            return (Heat2Writer writer, T value, bool _) =>
            {
                if (value is Tdf tdf)
                    writer.Encoder.VisitTdf(tdf);

                writer.Stream.WriteByte(0); // terminator
            };

        if (type.IsEnum)
            return createEnumTypeWriter<T>(type);

        if (Helpers.IsList(type))
        {
            Type[] typeArgs = type.GetGenericArguments();
            return (TypeWriter<T>)_createListWriterMethod.MakeGenericMethod(typeArgs).Invoke(null, new object[] { type })!;
        }

        if (Helpers.IsDictionary(type))
        {
            Type[] typeArgs = type.GetGenericArguments();
            return (TypeWriter<T>)_createMapWriterMethod.MakeGenericMethod(typeArgs).Invoke(null, new object[] { type })!;
        }

        // Unknown type :(
        return (Heat2Writer writer, T value, bool _) =>
        {
            throw new NotSupportedException($"Type is not supported: {typeof(T).FullName}");
        };
    }


    public void WriteHeader(uint tag, Heat2Type type)
    {
        //Header is 0xTTTTTTHH
        // T = Tag
        // H = Heat2 type
        Span<byte> headerBytes =
        [
            (byte)((tag >> 24) & 0xFF),
            (byte)((tag >> 16) & 0xFF),
            (byte)((tag >> 8) & 0xFF),
            (byte)type,
        ];

        Stream.Write(headerBytes);
    }

    public void WriteUnion<TUnion>(TUnion value) where TUnion : Union, new()
    {
        Stream.WriteByte(value.ActiveIndex);
        Encoder.VisitTdf(value);
    }

    public void WriteStruct<TStruct>(TStruct value) where TStruct : Tdf?, new()
    {
        if (value != null)
            Encoder.VisitTdf(value);
        Stream.WriteByte(0); // terminator
    }

    public void WriteVarInt(long value)
    {
        if (value == 0)
        {
            Stream.WriteByte(0);
            return;
        }

        if (value == long.MinValue)
        {
            //int64 min value is encoded as minus zero (-0)
            Stream.WriteByte(0x40); // zeroed six bits (value 0), negative (1), nextbit (0)
            return;
        }

        byte curByte;
        if (value > 0)
            curByte = (byte)(value & 0x3F | 0x80); //set first six bits + pos sign bit (0) + and next bit (1)
        else
        {
            value = -value;
            curByte = (byte)(value & 0x3F | 0xC0); //set first six bits + neg sign bit (1) + and next bit (1)
        }

        for (long i = value >> 6; i > 0; i >>= 7)
        {
            Stream.WriteByte(curByte);
            curByte = (byte)((i | 0x80) & 0xFF);
        }

        Stream.WriteByte((byte)(curByte & 0x7F)); //change next bit to 0
    }

    public void WriteString(string value)
    {
        int byteCount = StringEncoder.GetByteCount(value);
        WriteVarInt(byteCount + 1); // +1 for null terminator

        byte[]? rentedPool = null;

        Span<byte> bytes =
            byteCount > MaxStackAlloc
            ? new Span<byte>(rentedPool = ArrayPool<byte>.Shared.Rent(byteCount), 0, byteCount)
            : stackalloc byte[byteCount];

        int bytesReceived = StringEncoder.GetBytes(value, bytes);
        Debug.Assert(byteCount == bytesReceived);

        Stream.Write(bytes);
        Stream.WriteByte(0); // null terminator

        if (rentedPool != null)
            ArrayPool<byte>.Shared.Return(rentedPool);
    }

    public void WriteBool(bool value)
    {
        WriteVarInt(value ? 1 : 0);
    }

    public void WriteInt8(sbyte value)
    {
        WriteVarInt(value);
    }

    public void WriteUInt8(byte value)
    {
        WriteVarInt(value);
    }

    public void WriteInt16(short value)
    {
        WriteVarInt(value);
    }

    public void WriteUInt16(ushort value)
    {
        WriteVarInt(value);
    }

    public void WriteInt32(int value)
    {
        WriteVarInt(value);
    }

    public void WriteUInt32(uint value)
    {
        WriteVarInt(value);
    }

    public void WriteInt64(long value)
    {
        WriteVarInt(value);
    }

    public void WriteUInt64(ulong value)
    {
        WriteVarInt(unchecked((long)value));
    }

    public void WriteObjectType(ObjectType value)
    {
        WriteUInt16(value.Component);
        WriteUInt16(value.Type);
    }

    public void WriteObjectId(ObjectId value)
    {
        WriteObjectType(value.Type);
        WriteInt64(value.Id);
    }

    public void WriteBlob(byte[] value)
    {
        WriteInt32(value.Length);
        Stream.Write(value);
    }

    public void WriteEnum<TEnum>(TEnum value) where TEnum : new()
    {
        Type valueType = typeof(TEnum);
        TypeWriter<TEnum> writer = (TypeWriter<TEnum>)_typeWriters.GetOrAdd(valueType, createEnumTypeWriter<TEnum>);
        writer(this, value, visitHeader: true);
    }

    static TypeWriter<TEnum> createEnumTypeWriter<TEnum>(Type type)
    {
        return Type.GetTypeCode(typeof(TEnum)) switch
        {
            TypeCode.SByte => (Heat2Writer writer, TEnum value, bool _) => writer.WriteInt8(Convert.ToSByte(value)),
            TypeCode.Byte => (Heat2Writer writer, TEnum value, bool _) => writer.WriteUInt8(Convert.ToByte(value)),
            TypeCode.Int16 => (Heat2Writer writer, TEnum value, bool _) => writer.WriteInt16(Convert.ToInt16(value)),
            TypeCode.UInt16 => (Heat2Writer writer, TEnum value, bool _) => writer.WriteUInt16(Convert.ToUInt16(value)),
            TypeCode.Int32 => (Heat2Writer writer, TEnum value, bool _) => writer.WriteInt32(Convert.ToInt32(value)),
            TypeCode.UInt32 => (Heat2Writer writer, TEnum value, bool _) => writer.WriteUInt32(Convert.ToUInt32(value)),
            TypeCode.Int64 => (Heat2Writer writer, TEnum value, bool _) => writer.WriteInt64(Convert.ToInt64(value)),
            TypeCode.UInt64 => (Heat2Writer writer, TEnum value, bool _) => writer.WriteUInt64(Convert.ToUInt64(value)),
            _ => (Heat2Writer writer, TEnum value, bool _) => throw new NotSupportedException($"Enum type is not supported: {typeof(TEnum).FullName}"),
        };
    }

    public void WriteFloat(float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        Stream.Write(bytes);
    }

    public void WriteList<T>(IList<T> value)
    {
        Type valueType = typeof(IList<T>);
        TypeWriter<IList<T>> writer = (TypeWriter<IList<T>>)_typeWriters.GetOrAdd(valueType, createListTypeWriter<T>);
        writer(this, value, visitHeader: true);
    }

    static TypeWriter<IList<T>> createListTypeWriter<T>(Type listType)
    {
        TypeWriter<T> itemWriter = GetTypeWriter<T>();
        Heat2Type? itemHeatTypeNullable = Heat2Util.ToHeat2Type(typeof(T));

        if (itemHeatTypeNullable == null)
            return (Heat2Writer writer, IList<T> value, bool _) => throw new NotSupportedException($"List type is not supported: {typeof(T).FullName}");

        Heat2Type itemHeatType = itemHeatTypeNullable.Value;
        bool isMisencodable = itemHeatType == Heat2Type.Union || itemHeatType == Heat2Type.List || itemHeatType == Heat2Type.Map;


        return (Heat2Writer writer, IList<T> value, bool _) =>
        {
            Heat2Type type = itemHeatType;
            if (writer.Heat1BackCompatibility && isMisencodable)
                type = Heat2Type.Struct;

            writer.WriteHeat2Type(type);
            writer.WriteVarInt(value.Count);

            foreach (T item in value)
                itemWriter(writer, item, visitHeader: false);
        };
    }

    private void WriteHeat2Type(Heat2Type itemHeatType)
    {
        Stream.WriteByte((byte)itemHeatType);
    }

    public void WriteMap<TKey, TValue>(IDictionary<TKey, TValue> value) where TKey : notnull
    {
        Type valueType = typeof(IDictionary<TKey, TValue>);
        TypeWriter<IDictionary<TKey, TValue>> writer = (TypeWriter<IDictionary<TKey, TValue>>)_typeWriters.GetOrAdd(valueType, createMapTypeWriter<TKey, TValue>);
        writer(this, value, visitHeader: true);
    }

    static TypeWriter<IDictionary<TKey, TValue>> createMapTypeWriter<TKey, TValue>(Type mapType) where TKey : notnull
    {
        TypeWriter<TKey> keyWriter = GetTypeWriter<TKey>();
        TypeWriter<TValue> valueWriter = GetTypeWriter<TValue>();

        Heat2Type? keyHeatTypeNullable = Heat2Util.ToHeat2Type(typeof(TKey));
        if (keyHeatTypeNullable == null)
            return (Heat2Writer writer, IDictionary<TKey, TValue> value, bool _) => throw new NotSupportedException($"Map key type is not supported: {typeof(TKey).FullName}");

        Heat2Type? valueHeatTypeNullable = Heat2Util.ToHeat2Type(typeof(TValue));
        if (valueHeatTypeNullable == null)
            return (Heat2Writer writer, IDictionary<TKey, TValue> value, bool _) => throw new NotSupportedException($"Map value type is not supported: {typeof(TValue).FullName}");

        Heat2Type keyHeatType = keyHeatTypeNullable.Value;
        Heat2Type valueHeatType = valueHeatTypeNullable.Value;

        return (Heat2Writer writer, IDictionary<TKey, TValue> value, bool _) =>
        {
            writer.WriteHeat2Type(keyHeatType);
            writer.WriteHeat2Type(valueHeatType);
            writer.WriteVarInt(value.Count);

            foreach (KeyValuePair<TKey, TValue> pair in value)
            {
                keyWriter(writer, pair.Key, visitHeader: true);
                valueWriter(writer, pair.Value, visitHeader: true);
            }
        };
    }

    public void WriteTimeValue(TimeValue value)
    {
        WriteInt64(value.Microseconds);
    }

    public void WriteVariable(object? value)
    {
        if (value == null)
        {
            Stream.WriteByte(0x00);
            return;
        }

        Stream.WriteByte(0x01);

        if (value is Tdf tdf)
        {
            WriteUInt32(tdf.GetTdfId());
            Encoder.VisitTdf(tdf);
            Stream.WriteByte(0x00);
            return;
        }

        WriteUInt32(0);

        Type valueType = value.GetType();
        Heat2Type? heat2Type = Heat2Util.ToHeat2Type(valueType);
        if (heat2Type == null)
            throw new NotSupportedException($"Variable value type is not supported: {valueType.FullName}");

        WriteHeader(0, heat2Type.Value);

        switch (heat2Type.Value)
        {
            case Heat2Type.Integer:
                if (value is ulong ul)
                    WriteVarInt(unchecked((long)ul));
                else
                    WriteVarInt(Convert.ToInt64(value));
                break;
            case Heat2Type.String:
                WriteString((string)value);
                break;
            case Heat2Type.Binary:
                WriteBlob((byte[])value);
                break;
            case Heat2Type.Float:
                WriteFloat((float)value);
                break;
            case Heat2Type.BlazeObjectType:
                WriteObjectType((ObjectType)value);
                break;
            case Heat2Type.BlazeObjectId:
                WriteObjectId((ObjectId)value);
                break;
            default:
                throw new NotSupportedException($"Variable value type is not supported: {valueType.FullName}");
        }

        Stream.WriteByte(0x00);
    }
}

