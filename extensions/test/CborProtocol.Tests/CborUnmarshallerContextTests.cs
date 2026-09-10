using Amazon.Extensions.CborProtocol;
using Amazon.Extensions.CborProtocol.Internal.Transform;
using System;
using System.Formats.Cbor;
using System.IO;
using System.Linq;
using Xunit;

namespace Amazon.CborProtocol.Tests;


public class BufferSizeConfigFixture : IDisposable
{
    private readonly int _originalValue;

    public BufferSizeConfigFixture()
    {
        // Save original value
        _originalValue = AWSConfigs.CborReaderInitialBufferSize;

        // Set a small buffer size to easily test larger streams.
        AWSConfigs.CborReaderInitialBufferSize = 100;
    }

    public void Dispose()
    {
        // Restore original value
        AWSConfigs.CborReaderInitialBufferSize = _originalValue;
    }
}

public class CborUnmarshallerContextTests : IClassFixture<BufferSizeConfigFixture>
{
    private static CborUnmarshallerContext CreateContext(Stream stream)
        => new CborUnmarshallerContext(stream, maintainResponseBody: false, responseData: null);

    // Helpers mirroring how the SDK's unmarshallers drive the reader: a refilling
    // context.PeekState() before each single-token read (which guarantees that read
    // cannot run past the end of the buffer), and the context's chunk-aware methods
    // for values that may consume multiple tokens (text and byte strings, decimals).
    private static string ReadTextString(CborUnmarshallerContext context)
        => context.ReadTextString();

    private static byte[] ReadByteString(CborUnmarshallerContext context)
        => context.ReadByteString();

    private static int ReadInt32(CborUnmarshallerContext context)
    {
        context.PeekState();
        return context.Reader.ReadInt32();
    }

    private static int? ReadStartMap(CborUnmarshallerContext context)
    {
        context.PeekState();
        return context.Reader.ReadStartMap();
    }

    private static int? ReadStartArray(CborUnmarshallerContext context)
    {
        context.PeekState();
        return context.Reader.ReadStartArray();
    }

    private static void ReadEndMap(CborUnmarshallerContext context)
    {
        context.PeekState();
        context.Reader.ReadEndMap();
    }

    private static void ReadEndArray(CborUnmarshallerContext context)
    {
        context.PeekState();
        context.Reader.ReadEndArray();
    }

    private static CborTag ReadTag(CborUnmarshallerContext context)
    {
        context.PeekState();
        return context.Reader.ReadTag();
    }

    [Fact]
    public void Unmarshall_SimpleObject_FitsInInitialBuffer()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(2);
        writer.WriteTextString("Key1");
        writer.WriteTextString("Value1");
        writer.WriteTextString("Key2");
        writer.WriteInt32(123);
        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("Key1", ReadTextString(context));
        Assert.Equal("Value1", ReadTextString(context));
        Assert.Equal("Key2", ReadTextString(context));
        context.SkipValue(); // Skip integer
        ReadEndMap(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());

    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Unmarshall_ItemLargerThanBuffer_ResizesMultipleTimes(int multiplier)
    {
        // Create a string that will force multiple buffer resizes.
        int stringSize = (AWSConfigs.CborReaderInitialBufferSize * multiplier) + 50;
        var veryLargeString = new string('B', stringSize);

        var writer = new CborWriter();
        writer.WriteTextString(veryLargeString);
        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        string result = ReadTextString(context);
        Assert.Equal(veryLargeString, result);

    }

    [Fact]
    public void Unmarshall_ObjectCrossesBufferBoundary_RefillsCorrectly()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);
        writer.WriteTextString("key1");
        writer.WriteTextString(new string('A', 90));
        writer.WriteTextString("key2");
        writer.WriteStartMap(null);
        writer.WriteTextString("key3");
        writer.WriteTextString("value2");
        writer.WriteEndMap();
        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("key1", ReadTextString(context));
        string value1 = ReadTextString(context); // This read will consume most of the first chunk.
        Assert.Equal(90, value1.Length);
        Assert.Equal("key2", ReadTextString(context));
        ReadStartMap(context);

        Assert.Equal("key3", ReadTextString(context));
        Assert.Equal("value2", ReadTextString(context));
        ReadEndMap(context);
        ReadEndMap(context);

    }

    [Fact]
    public void SkipValue_OnLargeNestedMap_Succeeds()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(2);

        // Start the large nested map
        writer.WriteStartMap(1);
        writer.WriteTextString("large_key");
        writer.WriteTextString(new string('X', 300)); // This will force refills
        writer.WriteEndMap();

        // The value we want to read after the skip
        writer.WriteInt32(99);
        writer.WriteEndArray();

        var stream = new MemoryStream(writer.Encode());

        using var context = CreateContext(stream);

        ReadStartArray(context);

        // Skip the entire large map object
        context.SkipValue();

        // Assert that we can correctly read the next item
        Assert.Equal(99, ReadInt32(context));

        ReadEndArray(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());

    }

    [Fact]
    public void ReadEndArray_WhenInMap_ThrowsInvalidOperationException()
    {
        // Arrange: A simple map
        var writer = new CborWriter();
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        var stream = new MemoryStream(writer.Encode());

        // Act & Assert
        using var context = CreateContext(stream);

        ReadStartMap(context);
        // CborReader reports a container type mismatch as InvalidOperationException.
        Assert.Throws<InvalidOperationException>(() => ReadEndArray(context));

    }

    [Fact]
    public void Unmarshall_IncompleteStream_ThrowsCborContentException()
    {
        // CBOR header for a string of length 20, but we only provide 10 bytes of data.
        byte[] bytes = Convert.FromBase64String("eBQKCgoKCgoKCg==");
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        // The reader will attempt to read the string, realize it's incomplete,
        // try to refill, find no more data, and then throw an exception.
        Assert.Throws<CborContentException>(() => ReadTextString(context));

    }


    [Fact]
    public void Unmarshall_EmptyStream_ThrowsOnRead()
    {
        byte[] bytes = new byte[0];
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        // Attempting to read from a finished reader should throw.
        Assert.Throws<CborContentException>(() => ReadStartMap(context));

    }

    [Fact]
    public void Unmarshall_ComplexObject_SpansMultipleBuffers()
    {
        // Create a single map object with three large items.
        var writer = new CborWriter();
        writer.WriteStartMap(3);

        writer.WriteTextString("item1");
        writer.WriteTextString(new string('A', 80)); // Item 1

        writer.WriteTextString("item2");
        writer.WriteTextString(new string('B', 80)); // Item 2 (will cross boundary)

        writer.WriteTextString("item3");
        writer.WriteTextString(new string('C', 80)); // Item 3 (in second and third chunk)

        writer.WriteEndMap();
        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);

        Assert.Equal("item1", ReadTextString(context));
        Assert.Equal(80, ReadTextString(context).Length);

        Assert.Equal("item2", ReadTextString(context));
        Assert.Equal(80, ReadTextString(context).Length);

        Assert.Equal("item3", ReadTextString(context));
        Assert.Equal(80, ReadTextString(context).Length);

        ReadEndMap(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());

    }

    [Fact]
    public void Unmarshall_DefiniteLength_NestedMapAndArray()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(1);
        writer.WriteTextString("nestedArray");
        writer.WriteStartArray(2);
        writer.WriteInt32(1);
        writer.WriteInt32(2);
        writer.WriteEndArray();
        writer.WriteEndMap();

        var stream = new MemoryStream(writer.Encode());

        using var context = CreateContext(stream);
        ReadStartMap(context);
        Assert.Equal("nestedArray", ReadTextString(context));
        ReadStartArray(context);
        Assert.Equal(1, ReadInt32(context));
        Assert.Equal(2, ReadInt32(context));
        ReadEndArray(context);
        ReadEndMap(context);
    }

    [Fact]
    public void Unmarshall_IndefiniteLengthArray_CrossesBuffer_EndsWithBreak()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(null);
        writer.WriteTextString(new string('X', 90)); // Fills buffer
        writer.WriteTextString("done");
        writer.WriteEndArray();

        var stream = new MemoryStream(writer.Encode());

        using var context = CreateContext(stream);
        ReadStartArray(context);
        Assert.Equal(90, ReadTextString(context).Length);
        Assert.Equal("done", ReadTextString(context));
        ReadEndArray(context);
    }

    [Fact]
    public void Unmarshall_NestedIndefinite_MapArrayStructure()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);
        writer.WriteTextString("array");
        writer.WriteStartArray(null);
        writer.WriteTextString("one");
        writer.WriteTextString("two");
        writer.WriteEndArray();
        writer.WriteEndMap();

        var stream = new MemoryStream(writer.Encode());

        using var context = CreateContext(stream);
        ReadStartMap(context);
        Assert.Equal("array", ReadTextString(context));
        ReadStartArray(context);
        Assert.Equal("one", ReadTextString(context));
        Assert.Equal("two", ReadTextString(context));
        ReadEndArray(context);
        ReadEndMap(context);
    }

    [Fact]
    public void Unmarshall_EmptyIndefiniteArray()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(null);
        writer.WriteEndArray();

        var stream = new MemoryStream(writer.Encode());

        using var context = CreateContext(stream);
        ReadStartArray(context);
        ReadEndArray(context);
    }

    [Fact]
    public void Unmarshall_IncompleteMap_ThrowsCborContentException()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(1);
        writer.WriteTextString("key");
        writer.WriteTextString("value");
        writer.WriteEndMap();

        var bytes = writer.Encode().Take(5).ToArray(); // Truncate it
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);
        ReadStartMap(context);
        Assert.Equal("key", ReadTextString(context));
        Assert.Throws<CborContentException>(() => ReadTextString(context));
    }

    [Fact]
    public void Unmarshall_SkipValue_CrossesBufferBoundary()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(2);
        writer.WriteTextString(new string('X', 200));
        writer.WriteInt32(42);
        writer.WriteEndArray();

        var stream = new MemoryStream(writer.Encode());

        using var context = CreateContext(stream);
        ReadStartArray(context);
        context.SkipValue(); // This string skip will require buffer refill
        Assert.Equal(42, ReadInt32(context));
        ReadEndArray(context);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(ushort.MaxValue / 2)]
    [InlineData(ushort.MaxValue * 2)]
    public void Unmarshall_ByteString_CrossingBuffer(int length)
    {
        var writer = new CborWriter();
        byte[] largeByteArray = new byte[length];
        new Random().NextBytes(largeByteArray);

        writer.WriteStartArray(1);
        writer.WriteByteString(largeByteArray);
        writer.WriteEndArray();

        using var context = CreateContext(new MemoryStream(writer.Encode()));

        ReadStartArray(context);
        byte[] result = ReadByteString(context);
        ReadEndArray(context);

        Assert.Equal(largeByteArray, result);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(ushort.MaxValue / 2)]
    [InlineData(ushort.MaxValue * 2)]
    public void Unmarshall_TextString_CrossingBuffer(int length)
    {
        var writer = new CborWriter();
        var textString = new string('X', length);

        writer.WriteStartArray(1);
        writer.WriteTextString(textString);
        writer.WriteEndArray();

        using var context = CreateContext(new MemoryStream(writer.Encode()));
        ReadStartArray(context);
        var result = ReadTextString(context);
        ReadEndArray(context);

        Assert.Equal(textString, result);
    }

    [Theory]
    [InlineData(30)]   // chunks fit in the initial buffer
    [InlineData(80)]   // chunk boundaries interleave with buffer boundaries
    [InlineData(250)]  // single chunk larger than the initial buffer
    public void Unmarshall_IndefiniteLengthTextString_ReadChunkByChunk(int chunkSize)
    {
        var writer = new CborWriter(convertIndefiniteLengthEncodings: false);
        writer.WriteStartArray(2);
        writer.WriteStartIndefiniteLengthTextString();
        writer.WriteTextString(new string('A', chunkSize));
        writer.WriteTextString(new string('B', chunkSize));
        writer.WriteTextString(new string('C', chunkSize));
        writer.WriteEndIndefiniteLengthTextString();
        writer.WriteInt32(7);
        writer.WriteEndArray();

        var stream = new MemoryStream(writer.Encode());
        using var context = CreateContext(stream);

        ReadStartArray(context);
        string result = ReadTextString(context);
        Assert.Equal(new string('A', chunkSize) + new string('B', chunkSize) + new string('C', chunkSize), result);
        Assert.Equal(7, ReadInt32(context));
        ReadEndArray(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(80)]
    [InlineData(250)]
    public void Unmarshall_IndefiniteLengthByteString_ReadChunkByChunk(int chunkSize)
    {
        var chunk1 = new byte[chunkSize];
        var chunk2 = new byte[chunkSize];
        new Random(42).NextBytes(chunk1);
        new Random(43).NextBytes(chunk2);

        var writer = new CborWriter(convertIndefiniteLengthEncodings: false);
        writer.WriteStartIndefiniteLengthByteString();
        writer.WriteByteString(chunk1);
        writer.WriteByteString(chunk2);
        writer.WriteEndIndefiniteLengthByteString();

        var stream = new MemoryStream(writer.Encode());
        using var context = CreateContext(stream);

        byte[] result = ReadByteString(context);
        Assert.Equal(chunk1.Concat(chunk2).ToArray(), result);
        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Theory]
    [InlineData("3.14159")]
    [InlineData("-0.000001")]
    [InlineData("79228162514264337593543950335")] // decimal.MaxValue, forces a bignum mantissa
    public void Unmarshall_Decimal_CrossingBufferBoundary(string decimalText)
    {
        var expected = decimal.Parse(decimalText, System.Globalization.CultureInfo.InvariantCulture);

        // Pad with a large string so the decimal lands near a buffer boundary.
        var writer = new CborWriter();
        writer.WriteStartArray(2);
        writer.WriteTextString(new string('X', 95));
        writer.WriteDecimal(expected);
        writer.WriteEndArray();

        var stream = new MemoryStream(writer.Encode());
        using var context = CreateContext(stream);

        ReadStartArray(context);
        Assert.Equal(95, ReadTextString(context).Length);
        Assert.Equal(expected, context.ReadDecimal());
        ReadEndArray(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_CborTags()
    {
        var writer = new CborWriter();
        writer.WriteTag(CborTag.DateTimeString);
        writer.WriteTextString("2025-07-29T00:00:00Z");

        using var context = CreateContext(new MemoryStream(writer.Encode()));
        CborTag tag = ReadTag(context);
        string dateStr = ReadTextString(context);

        Assert.Equal(CborTag.DateTimeString, tag);
        Assert.Equal("2025-07-29T00:00:00Z", dateStr);
    }

    [Fact]
    public void Unmarshall_DeeplyNestedStructure()
    {
        var writer = new CborWriter();
        // Create a deeply nested structure
        for (int i = 0; i < 10; i++)
        {
            writer.WriteStartMap(1);
            writer.WriteTextString($"level{i}");
        }
        writer.WriteTextString("value");
        for (int i = 0; i < 10; i++)
        {
            writer.WriteEndMap();
        }

        using var context = CreateContext(new MemoryStream(writer.Encode()));
        for (int i = 0; i < 10; i++)
        {
            ReadStartMap(context);
            Assert.Equal($"level{i}", ReadTextString(context));
        }
        Assert.Equal("value", ReadTextString(context));
        for (int i = 0; i < 10; i++)
        {
            ReadEndMap(context);
        }
    }

    [Fact]
    public void PeekState_DoesNotAssumeEnd_WhenValueIsInNextChunk()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);
        writer.WriteTextString("key");
        writer.WriteTextString(new string('X', 95)); // Forces a refill before the value is fully read
        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        using var stream = new MemoryStream(bytes);
        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("key", ReadTextString(context));

        // This should trigger a refill, not treat it as EndMap
        var value = ReadTextString(context);
        Assert.Equal(95, value.Length);

        ReadEndMap(context);
    }

    [Fact]
    public void Handles_BreakMarker_AtStartOfNextChunk()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(null);
        writer.WriteTextString(new string('A', 95)); // Fills buffer almost completely
        writer.WriteEndArray(); // Writes 0xFF as break byte

        var bytes = writer.Encode();
        using var stream = new MemoryStream(bytes);
        using var context = CreateContext(stream);

        ReadStartArray(context);
        string value = ReadTextString(context);

        // This will cause a refill where the 0xFF is the first byte of the new chunk
        ReadEndArray(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Handles_MultipleBreakMarkers_AtEndOfStream()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(null);
        writer.WriteStartArray(null);
        writer.WriteTextString("val");
        writer.WriteEndArray(); // Ends inner
        writer.WriteEndArray(); // Ends outer

        byte[] bytes = writer.Encode();
        using var stream = new MemoryStream(bytes);
        using var context = CreateContext(stream);

        ReadStartArray(context); // outer
        ReadStartArray(context); // inner
        Assert.Equal("val", ReadTextString(context));

        // Should skip 0xFF, refill, then skip another 0xFF
        ReadEndArray(context);
        ReadEndArray(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void DoesNotInferEndMap_WhenContentFollows()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(2);
        writer.WriteTextString("k1");
        writer.WriteTextString(new string('A', 80));
        writer.WriteTextString("k2");
        writer.WriteTextString(new string('B', 80));
        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        using var stream = new MemoryStream(bytes);
        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("k1", ReadTextString(context));
        Assert.Equal(80, ReadTextString(context).Length);

        Assert.Equal("k2", ReadTextString(context));

        // This should not throw or prematurely infer EndMap
        Assert.Equal(80, ReadTextString(context).Length);

        ReadEndMap(context);
    }

    [Fact]
    public void Handles_DefinedLengthMap_ThatEndsExactlyAtEof()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(1);
        writer.WriteTextString("key");
        writer.WriteTextString("value");
        writer.WriteEndMap(); // Definite-length, no break byte

        var bytes = writer.Encode();
        using var stream = new MemoryStream(bytes);
        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("key", ReadTextString(context));
        Assert.Equal("value", ReadTextString(context));
        ReadEndMap(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Handles_NestedMapEndAtBufferBoundary_FollowedByOuterKey()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);      // Outer map
        writer.WriteTextString("outerKey1");
        writer.WriteStartMap(null);      // Inner map
        writer.WriteTextString("innerKey");
        writer.WriteTextString("innerVal");
        writer.WriteEndMap();            // Ends inner map
        writer.WriteTextString("outerKey2");
        writer.WriteTextString("outerVal");
        writer.WriteEndMap();            // Ends outer map

        byte[] bytes = writer.Encode();

        using var stream = new MemoryStream(bytes);
        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("outerKey1", ReadTextString(context));
        ReadStartMap(context);
        Assert.Equal("innerKey", ReadTextString(context));
        Assert.Equal("innerVal", ReadTextString(context));

        // This ReadEndMap will bring us to the end of current buffer
        ReadEndMap(context); // This will trigger refill internally if needed

        // Without a refill, the next ReadTextString() throws:
        // "No more CBOR data items to read in the current context."
        Assert.Equal("outerKey2", ReadTextString(context));
        Assert.Equal("outerVal", ReadTextString(context));
        ReadEndMap(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void ReadTextString_TriggersRefill_After_ReadEndMap_AtBufferEnd()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);              // Outer map
        writer.WriteTextString("outerKey1");
        writer.WriteStartMap(null);              // Inner map
        writer.WriteTextString("innerKey");
        writer.WriteTextString(new string('A', 30)); // Ensure we consume most of the buffer
        writer.WriteEndMap();                    // End inner map — should still fit in buffer
        writer.WriteTextString("outerKey2");     // Starts in next refill
        writer.WriteTextString("outerVal");
        writer.WriteEndMap();                    // End outer map

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);                   // outer map
        Assert.Equal("outerKey1", ReadTextString(context));

        ReadStartMap(context);                   // inner map
        Assert.Equal("innerKey", ReadTextString(context));
        Assert.Equal(new string('A', 30), ReadTextString(context));

        ReadEndMap(context);                     // This must succeed without triggering refill

        // This next read should trigger refill and continue parsing correctly
        Assert.Equal("outerKey2", ReadTextString(context));
        Assert.Equal("outerVal", ReadTextString(context));
        ReadEndMap(context);                     // outer map
        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_MultipleNestedIndefiniteMapsEndingAtStreamEnd_ShouldSucceed()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(null);
        writer.WriteTextString("nested");
        writer.WriteStartMap(null);
        writer.WriteTextString("deep");
        writer.WriteTextString("value");
        writer.WriteEndMap(); // emits 0xFF
        writer.WriteEndMap(); // emits 0xFF

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);
        ReadStartMap(context);
        Assert.Equal("nested", ReadTextString(context));
        ReadStartMap(context);
        Assert.Equal("deep", ReadTextString(context));
        Assert.Equal("value", ReadTextString(context));
        ReadEndMap(context); // should correctly handle 0xFF at end
        ReadEndMap(context); // should correctly handle 0xFF at end
    }

    [Fact]
    public void PeekState_DefiniteArrayAcrossBuffer_ReturnsEndArrayBeforeNextValue()
    {
        var writer = new CborWriter(allowMultipleRootLevelValues: true);
        writer.WriteStartArray(3);
        writer.WriteInt32(1);
        writer.WriteTextString(new string('X', 200));
        writer.WriteInt32(3);
        writer.WriteEndArray();
        writer.WriteInt32(99); // trailing value outside array

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartArray(context);
        Assert.Equal(1, ReadInt32(context));
        Assert.Equal(200, ReadTextString(context).Length);
        Assert.Equal(3, ReadInt32(context));

        // PeekState should return EndArray before the trailing value
        Assert.Equal(CborReaderState.EndArray, context.PeekState());

        // Reading EndArray moves reader to the next value
        ReadEndArray(context);
        Assert.Equal(CborReaderState.UnsignedInteger, context.PeekState());
    }

    [Fact]
    public void PeekState_DefiniteMapAcrossBuffer_ReturnsEndMapBeforeNextValue()
    {
        var writer = new CborWriter(allowMultipleRootLevelValues: true);
        writer.WriteStartMap(2);
        writer.WriteTextString("a");
        writer.WriteInt32(1);
        writer.WriteTextString("b");
        writer.WriteTextString(new string('X', 200));
        writer.WriteEndMap();
        writer.WriteInt32(99); // trailing value outside map

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);
        Assert.Equal("a", ReadTextString(context));
        Assert.Equal(1, ReadInt32(context));
        Assert.Equal("b", ReadTextString(context));
        Assert.Equal(200, ReadTextString(context).Length);

        // PeekState should return EndMap before the trailing value
        Assert.Equal(CborReaderState.EndMap, context.PeekState());

        // Reading EndMap moves reader to the next value
        ReadEndMap(context);
        Assert.Equal(CborReaderState.UnsignedInteger, context.PeekState());
    }

    [Fact]
    public void PeekState_LongDefiniteArray_FitsAcrossMultipleBuffers()
    {
        var writer = new CborWriter();
        writer.WriteStartArray(100); // large array to exceed 100-byte buffer
        for (int i = 0; i < 100; i++)
        {
            writer.WriteInt32(i);
        }
        writer.WriteEndArray();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartArray(context);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, ReadInt32(context));
        }

        // PeekState should correctly report EndArray after last element
        Assert.Equal(CborReaderState.EndArray, context.PeekState());
    }

    [Fact]
    public void PeekState_LongDefiniteMap_FitsAcrossMultipleBuffers()
    {
        var writer = new CborWriter();
        writer.WriteStartMap(25); // large map, each entry has key+value
        for (int i = 0; i < 25; i++)
        {
            writer.WriteTextString("k" + i);
            writer.WriteInt32(i);
        }
        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);
        for (int i = 0; i < 25; i++)
        {
            Assert.Equal("k" + i, ReadTextString(context));
            Assert.Equal(i, ReadInt32(context));
        }

        // PeekState should correctly report EndMap after last entry
        Assert.Equal(CborReaderState.EndMap, context.PeekState());
    }


    [Fact]
    public void Unmarshall_NestedContainers_UpdatesParentItemCountCorrectly()
    {
        var writer = new CborWriter();

        writer.WriteStartMap(2);

        writer.WriteTextString(new string('A', 100));

        writer.WriteStartMap(1);
        writer.WriteTextString("InnerKey");

        writer.WriteStartArray(2);
        writer.WriteTextString(new string('A', 100));
        writer.WriteInt32(20);
        writer.WriteEndArray();

        writer.WriteEndMap();

        writer.WriteTextString("Key2");

        writer.WriteInt32(42);

        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);

        Assert.Equal(100, ReadTextString(context).Length);
        ReadStartMap(context);
        Assert.Equal("InnerKey", ReadTextString(context));
        ReadStartArray(context);
        Assert.Equal(100, ReadTextString(context).Length);
        Assert.Equal(20, ReadInt32(context));
        ReadEndArray(context);
        ReadEndMap(context);

        // Key2 -> inner map
        Assert.Equal(CborReaderState.TextString, context.PeekState());
        Assert.Equal("Key2", ReadTextString(context));
        Assert.Equal(42, ReadInt32(context));
        ReadEndMap(context);


        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_NestedArrays_CrossBufferBoundary_RefillsCorrectly()
    {
        var writer = new CborWriter();

        writer.WriteStartArray(5);

        writer.WriteStartArray(1);
        writer.WriteTextString(new string('A', 50));
        writer.WriteEndArray();

        writer.WriteStartArray(1);
        writer.WriteTextString(new string('B', 50));
        writer.WriteEndArray();

        writer.WriteStartArray(1);
        writer.WriteTextString(new string('C', 50));
        writer.WriteEndArray();

        writer.WriteStartArray(1);
        writer.WriteTextString(new string('D', 50));
        writer.WriteEndArray();

        writer.WriteInt32(42);

        writer.WriteEndArray();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartArray(context);

        // Inner arrays
        ReadStartArray(context);
        Assert.Equal(new string('A', 50), ReadTextString(context));
        ReadEndArray(context);

        ReadStartArray(context);
        Assert.Equal(new string('B', 50), ReadTextString(context));
        ReadEndArray(context);

        ReadStartArray(context);
        Assert.Equal(new string('C', 50), ReadTextString(context));
        ReadEndArray(context);

        ReadStartArray(context);
        Assert.Equal(new string('D', 50), ReadTextString(context));
        ReadEndArray(context);

        // PeekState should correctly indicate we are still inside the outer array
        Assert.Equal(CborReaderState.UnsignedInteger, context.PeekState());

        // Second item in outer array
        Assert.Equal(42, ReadInt32(context));

        ReadEndArray(context);
        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_NestedArrays_MixedLengthsAcrossBuffers()
    {
        var writer = new CborWriter();

        // Outer array: 3 items (definite length)
        writer.WriteStartArray(3);

        // Item 1: long string to force buffer refill
        writer.WriteTextString(new string('A', 150));

        // Item 2: definite-length array with 2 items
        writer.WriteStartArray(2);
        writer.WriteInt32(100);
        writer.WriteInt32(200);
        writer.WriteEndArray();

        // Item 3: indefinite-length array with mixed items
        writer.WriteStartArray(null);
        writer.WriteTextString("inner1");
        writer.WriteTextString(new string('B', 120)); // cross buffer
        writer.WriteEndArray();

        writer.WriteEndArray(); // End outer array

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartArray(context);

        // Item 1
        string longString = ReadTextString(context);
        Assert.Equal(150, longString.Length);

        // Item 2: definite-length array
        ReadStartArray(context);
        Assert.Equal(100, ReadInt32(context));
        Assert.Equal(200, ReadInt32(context));
        ReadEndArray(context);

        // Item 3: indefinite-length array
        ReadStartArray(context);
        Assert.Equal("inner1", ReadTextString(context));
        string longStringB = ReadTextString(context);
        Assert.Equal(120, longStringB.Length);
        ReadEndArray(context);

        ReadEndArray(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_NestedMaps_MixedLengthsAcrossBuffers()
    {
        var writer = new CborWriter();

        // Outer map: 2 pairs (definite length)
        writer.WriteStartMap(2);

        // Pair 1: key = "k1", value = definite-length map
        writer.WriteTextString("k1");
        writer.WriteStartMap(2);
        writer.WriteTextString("innerKey1");
        writer.WriteTextString(new string('X', 130)); // crosses buffer
        writer.WriteTextString("innerKey2");
        writer.WriteInt32(999);
        writer.WriteEndMap();

        // Pair 2: key = "k2", value = indefinite-length map
        writer.WriteTextString("k2");
        writer.WriteStartMap(null);
        writer.WriteTextString("innerKey3");
        writer.WriteTextString("innerVal3");
        writer.WriteTextString("innerKey4");
        writer.WriteTextString(new string('Y', 140)); // crosses buffer
        writer.WriteEndMap();

        writer.WriteEndMap(); // End outer map

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);

        // Pair 1
        Assert.Equal("k1", ReadTextString(context));
        ReadStartMap(context);
        Assert.Equal("innerKey1", ReadTextString(context));
        string innerVal1 = ReadTextString(context);
        Assert.Equal(130, innerVal1.Length);
        Assert.Equal("innerKey2", ReadTextString(context));
        Assert.Equal(999, ReadInt32(context));
        ReadEndMap(context);

        // Pair 2
        Assert.Equal("k2", ReadTextString(context));
        ReadStartMap(context);
        Assert.Equal("innerKey3", ReadTextString(context));
        Assert.Equal("innerVal3", ReadTextString(context));
        Assert.Equal("innerKey4", ReadTextString(context));
        string innerVal4 = ReadTextString(context);
        Assert.Equal(140, innerVal4.Length);
        ReadEndMap(context);

        ReadEndMap(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_NestedArrays_MixedDefiniteAndIndefiniteLengthsAcrossBuffers()
    {
        var writer = new CborWriter();

        // Outer array: 3 items (definite length)
        writer.WriteStartArray(3);

        writer.WriteTextString(new string('A', 150));

        // Item 2: definite-length array with 2 items
        writer.WriteStartArray(2);
        writer.WriteInt32(100);
        writer.WriteInt32(200);
        writer.WriteEndArray();

        // Item 3: indefinite-length array with mixed items
        writer.WriteStartArray(null);
        writer.WriteTextString("inner1");
        writer.WriteTextString(new string('B', 120));
        writer.WriteEndArray();

        writer.WriteEndArray();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartArray(context);

        // Item 1
        string longString = ReadTextString(context);
        Assert.Equal(150, longString.Length);

        // Item 2: definite-length array
        ReadStartArray(context);
        Assert.Equal(100, ReadInt32(context));
        Assert.Equal(200, ReadInt32(context));
        ReadEndArray(context);

        // Item 3: indefinite-length array
        ReadStartArray(context);
        Assert.Equal("inner1", ReadTextString(context));
        string longStringB = ReadTextString(context);
        Assert.Equal(120, longStringB.Length);
        ReadEndArray(context);

        ReadEndArray(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void Unmarshall_NestedMaps_MixedDefiniteAndIndefiniteLengthsAcrossBuffers()
    {
        var writer = new CborWriter();

        writer.WriteStartMap(2);

        // Pair 1: key = "k1", value = definite-length map
        writer.WriteTextString("k1");
        writer.WriteStartMap(2);
        writer.WriteTextString("innerKey1");
        writer.WriteTextString(new string('X', 130));
        writer.WriteTextString("innerKey2");
        writer.WriteInt32(999);
        writer.WriteEndMap();

        // Pair 2: key = "k2", value = indefinite-length map
        writer.WriteTextString("k2");
        writer.WriteStartMap(null);
        writer.WriteTextString("innerKey3");
        writer.WriteTextString("innerVal3");
        writer.WriteTextString("innerKey4");
        writer.WriteTextString(new string('Y', 140));
        writer.WriteEndMap();

        writer.WriteEndMap();

        byte[] bytes = writer.Encode();
        var stream = new MemoryStream(bytes);

        using var context = CreateContext(stream);

        ReadStartMap(context);

        // Pair 1
        Assert.Equal("k1", ReadTextString(context));
        ReadStartMap(context);
        Assert.Equal("innerKey1", ReadTextString(context));
        string innerVal1 = ReadTextString(context);
        Assert.Equal(130, innerVal1.Length);
        Assert.Equal("innerKey2", ReadTextString(context));
        Assert.Equal(999, ReadInt32(context));
        ReadEndMap(context);

        // Pair 2
        Assert.Equal("k2", ReadTextString(context));
        ReadStartMap(context);
        Assert.Equal("innerKey3", ReadTextString(context));
        Assert.Equal("innerVal3", ReadTextString(context));
        Assert.Equal("innerKey4", ReadTextString(context));
        string innerVal4 = ReadTextString(context);
        Assert.Equal(140, innerVal4.Length);
        ReadEndMap(context);

        ReadEndMap(context);

        Assert.Equal(CborReaderState.Finished, context.PeekState());
    }

    [Fact]
    public void ReadNestedMixedArraysAndMaps_WithDefiniteAndIndefiniteLengthsAndLongItemsAcrossBuffers_ShouldSucceed()
    {
        var writer = new CborWriter();

        writer.WriteStartArray(4);

        writer.WriteStartArray(2);
        writer.WriteInt32(1);
        writer.WriteTextString(new string('a', 150));
        writer.WriteEndArray();

        writer.WriteStartArray(null);
        writer.WriteStartArray(2);
        writer.WriteInt32(2);
        writer.WriteByteString(new byte[150]);
        writer.WriteEndArray();
        writer.WriteEndArray();

        writer.WriteStartMap(2);

        writer.WriteInt32(3);
        writer.WriteStartArray(2);
        writer.WriteTextString(new string('b', 150)); 
        writer.WriteInt32(4);
        writer.WriteEndArray();

        writer.WriteInt32(5);
        writer.WriteStartMap(1);
        writer.WriteInt32(6);
        writer.WriteByteString(new byte[150]);
        writer.WriteEndMap();

        writer.WriteEndMap();

        writer.WriteStartMap(1);
        writer.WriteInt32(7);
        writer.WriteStartMap(null);
        writer.WriteInt32(8);
        writer.WriteTextString(new string('c', 150));
        writer.WriteEndMap();
        writer.WriteEndMap();

        writer.WriteEndArray();

        var encoding = writer.Encode();

        using var stream = new MemoryStream(encoding);
        var context = CreateContext(stream);

        ReadStartArray(context);

        ReadStartArray(context);
        Assert.Equal(1, ReadInt32(context));
        var str1 = ReadTextString(context);
        Assert.Equal(new string('a', 150), str1);
        ReadEndArray(context);

        ReadStartArray(context);
        ReadStartArray(context);
        Assert.Equal(2, ReadInt32(context));
        var bytes1 = ReadByteString(context);
        Assert.Equal(150, bytes1.Length);
        ReadEndArray(context);
        ReadEndArray(context);

        ReadStartMap(context);

        Assert.Equal(3, ReadInt32(context));
        ReadStartArray(context);
        var str2 = ReadTextString(context);
        Assert.Equal(new string('b', 150), str2);
        Assert.Equal(4, ReadInt32(context));
        ReadEndArray(context);

        Assert.Equal(5, ReadInt32(context));
        ReadStartMap(context);
        Assert.Equal(6, ReadInt32(context));
        var bytes2 = ReadByteString(context);
        Assert.Equal(150, bytes2.Length);
        ReadEndMap(context);

        ReadEndMap(context);

        ReadStartMap(context);
        Assert.Equal(7, ReadInt32(context));
        ReadStartMap(context);
        Assert.Equal(8, ReadInt32(context));
        var str3 = ReadTextString(context);
        Assert.Equal(new string('c', 150), str3);
        ReadEndMap(context);
        ReadEndMap(context);

        ReadEndArray(context);
    }
}

