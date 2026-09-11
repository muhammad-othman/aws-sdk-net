/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.IO;
using System.Linq;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;

namespace Amazon.Extensions.CborProtocol.Internal.Transform
{
    public class CborUnmarshallerContext : UnmarshallerContext
    {
        // Incremental reading (isFinalBlock: false) requires Lax conformance.
        private static readonly CborReaderOptions _readerOptions = new CborReaderOptions
        {
            ConformanceMode = CborConformanceMode.Lax,
            AllowMultipleRootLevelValues = true,
        };

        private static readonly ILogger _logger = Logger.GetLogger(typeof(CborUnmarshallerContext));

        private bool disposed = false;
        private readonly Stack<string> _pathStack = new Stack<string>();

        private byte[] _buffer;
        private int _currentChunkSize;
        private bool _isFinalBlock;

        // There isn't a direct way to check if the reader is at the start of the document,
        // and we don't need it for the unmarshalling logic anyway.
        public override bool IsStartOfDocument => throw new NotImplementedException();

        public override bool IsEndElement => PeekState() == CborReaderState.Finished;
        public override bool IsStartElement => PeekState() == CborReaderState.StartMap;

        public Stream Stream { get; set; }

        /// <summary>
        /// The <see cref="CborReader"/> over the buffered response data. The reader operates in
        /// incremental mode: the context reads the response stream in chunks and supplies more data
        /// via <see cref="CborReader.SlideData"/> whenever the reader runs out.
        ///
        /// Single-token read methods (integers, booleans, definite-length strings, start/end of
        /// containers, tags) are safe to call directly immediately after <see cref="PeekState"/>
        /// has returned the state for that token: the peek guarantees the full token is buffered.
        /// Values that span multiple tokens must go through the context instead:
        /// <see cref="ReadTextString"/>, <see cref="ReadByteString"/> and <see cref="ReadDecimal"/>.
        /// </summary>
        public CborReader Reader { get; private set; }

        public override string CurrentPath => string.Join("/", _pathStack.Reverse());
        public override int CurrentDepth => Reader.CurrentDepth;

        public CborUnmarshallerContext(
            Stream responseStream,
            bool maintainResponseBody,
            IWebResponseData responseData,
            bool isException = false
        )
            : this(responseStream, maintainResponseBody, responseData, isException, null) { }

        public CborUnmarshallerContext(
            Stream responseStream,
            bool maintainResponseBody,
            IWebResponseData responseData,
            bool isException,
            IRequestContext requestContext
        )
        {
            if (isException)
            {
                WrappingStream = new CachingWrapperStream(responseStream);
            }
            else if (maintainResponseBody)
            {
                WrappingStream = new CachingWrapperStream(
                    responseStream,
                    AWSConfigs.LoggingConfig.LogResponsesSizeLimit
                );
            }

            if (isException || maintainResponseBody)
            {
                responseStream = WrappingStream;
            }

            if (responseData != null)
            {
                bool parsedContentLengthHeader = long.TryParse(
                    responseData.GetHeaderValue("Content-Length"),
                    out long contentLength
                );
                if (parsedContentLengthHeader && contentLength == 0)
                {
                    IsEmptyResponse = true;
                }

                if (
                    parsedContentLengthHeader
                    && responseData.ContentLength == contentLength
                    && string.IsNullOrEmpty(responseData.GetHeaderValue("Content-Encoding"))
                    && requestContext?.OriginalRequest?.CoreChecksumMode
                        == CoreChecksumResponseBehavior.ENABLED
                )
                {
                    base.SetupCRCStream(responseData, responseStream, contentLength);
                    base.SetupFlexibleChecksumStream(
                        responseData,
                        CrcStream ?? responseStream,
                        contentLength,
                        requestContext
                    );
                }
            }

            this.WebResponseData = responseData;
            this.MaintainResponseBody = maintainResponseBody;
            this.IsException = isException;

            Stream = FlexibleChecksumStream ?? CrcStream ?? responseStream;

            _buffer = ArrayPool<byte>.Shared.Rent(AWSConfigs.CborReaderInitialBufferSize);
            _currentChunkSize = Stream.Read(_buffer, 0, _buffer.Length);

            // Start in non-final mode; RefillBuffer flips to the final block once the stream is exhausted.
            var memorySlice = new ReadOnlyMemory<byte>(_buffer, 0, _currentChunkSize);
            Reader = new CborReader(memorySlice, _readerOptions, isFinalBlock: false);
        }

        /// <summary>
        /// Reads the next CBOR token state, reading more data from the response stream as needed.
        /// Once this method returns a token state, the full token is buffered, so the corresponding
        /// single-token read method on <see cref="Reader"/> is guaranteed not to fail due to an
        /// unexpected end of the data.
        /// </summary>
        public CborReaderState PeekState()
        {
            CborReaderState state;
            while ((state = Reader.PeekState()) == CborReaderState.NeedsMoreData)
            {
                RefillBuffer();
            }

            return state;
        }

        /// <summary>
        /// Skips the next CBOR value (including all of its nested content), reading more data from
        /// the response stream as needed.
        /// </summary>
        public void SkipValue()
        {
            // TrySkipValue returns false (leaving the reader state unchanged) only when the value
            // is incomplete in a non-final buffer, so keep refilling until the whole value is available.
            while (!Reader.TrySkipValue())
            {
                RefillBuffer();
            }
        }

        /// <summary>
        /// Reads a CBOR text string, reading more data from the response stream as needed.
        /// Definite-length strings are a single token, guaranteed to be fully buffered after the
        /// initial peek. Indefinite-length strings span multiple tokens, so they are read chunk by
        /// chunk with a refilling peek before each chunk.
        /// </summary>
        public string ReadTextString()
        {
            if (PeekState() != CborReaderState.StartIndefiniteLengthTextString)
            {
                // Definite-length string (or a type mismatch, which the reader will report).
                return Reader.ReadTextString();
            }

            Reader.ReadStartIndefiniteLengthTextString();
            var result = new StringBuilder();
            while (PeekState() != CborReaderState.EndIndefiniteLengthTextString)
            {
                // Chunks of an indefinite-length string are themselves definite-length strings.
                result.Append(Reader.ReadTextString());
            }

            Reader.ReadEndIndefiniteLengthTextString();
            return result.ToString();
        }

        /// <summary>
        /// Reads a CBOR byte string, reading more data from the response stream as needed.
        /// Definite-length strings are a single token, guaranteed to be fully buffered after the
        /// initial peek. Indefinite-length strings span multiple tokens, so they are read chunk by
        /// chunk with a refilling peek before each chunk.
        /// </summary>
        public byte[] ReadByteString()
        {
            if (PeekState() != CborReaderState.StartIndefiniteLengthByteString)
            {
                // Definite-length string (or a type mismatch, which the reader will report).
                return Reader.ReadByteString();
            }

            Reader.ReadStartIndefiniteLengthByteString();
            using (var result = new MemoryStream())
            {
                while (PeekState() != CborReaderState.EndIndefiniteLengthByteString)
                {
                    // Chunks of an indefinite-length string are themselves definite-length strings.
                    var chunk = Reader.ReadByteString();
                    result.Write(chunk, 0, chunk.Length);
                }

                Reader.ReadEndIndefiniteLengthByteString();
                return result.ToArray();
            }
        }

        // The largest reasonable encoding of a decimal fraction whose value fits a .NET decimal:
        // tag 4, array(2) header, and an int64 exponent at their maximum (non-canonical) width of
        // 9 bytes each, plus a bignum mantissa (tag 2/3 + byte string header, 9 bytes each) holding
        // the 12 bytes of a 96-bit decimal mantissa with a redundant leading zero: 58 bytes total.
        private const int LargestDecimalFractionEncodingSize = 64;

        /// <summary>
        /// Reads a CBOR decimal fraction (tag 4), reading more data from the response stream as
        /// needed. A decimal fraction spans multiple tokens (tag, array, exponent, mantissa), so
        /// <see cref="PeekState"/>'s single-token guarantee is not enough. Instead, the buffer is
        /// refilled until it holds at least <see cref="LargestDecimalFractionEncodingSize"/> bytes,
        /// after which the value is read directly.
        /// </summary>
        public decimal ReadDecimal()
        {
            while (Reader.BytesRemaining < LargestDecimalFractionEncodingSize && !_isFinalBlock)
            {
                RefillBuffer();
            }

            try
            {
                return Reader.ReadDecimal();
            }
            catch (CborContentException) when (!_isFinalBlock)
            {
                // Only reachable when the value is truncated at the end of the buffer despite the
                // sizing above, i.e. a decimal encoded with redundant padding (a leading-zero or
                // indefinite-length bignum mantissa) that exceeds the threshold. ReadDecimal rolls
                // the reader back on failure, so buffer the whole value by skipping it — TrySkipValue
                // only succeeds once the complete value is available — and re-parse its bytes, still
                // present in the buffer, with a standalone reader. A genuinely malformed value
                // surfaces the same error from the skip or the re-parse.
                int remainingBeforeValue;
                while (true)
                {
                    remainingBeforeValue = Reader.BytesRemaining;
                    if (Reader.TrySkipValue())
                    {
                        break;
                    }

                    RefillBuffer();
                }

                int valueStart = _currentChunkSize - remainingBeforeValue;
                int valueLength = remainingBeforeValue - Reader.BytesRemaining;

                var valueReader = new CborReader(new ReadOnlyMemory<byte>(_buffer, valueStart, valueLength), _readerOptions);
                return valueReader.ReadDecimal();
            }
        }

        /// <summary>
        /// Supplies the reader with more data once it has signaled that the next data item is
        /// incomplete in the current buffer. Unread bytes are preserved at the start of the
        /// buffer (growing it when the unread data fills it entirely), the next chunk is read
        /// from the stream, and the combined data is handed back to the reader via
        /// <see cref="CborReader.SlideData"/> without losing the current nesting context.
        /// </summary>
        private void RefillBuffer()
        {
            int leftoverBytesCount = Reader.BytesRemaining;
            int leftoverStartIndex = _currentChunkSize - leftoverBytesCount;

            // If the incomplete item is a definite-length string, size the buffer for the whole
            // token up front so it is read with a single grow-and-copy instead of repeated
            // doublings that each re-copy the accumulated data.
            int pendingTokenSize = GetPendingDefiniteLengthStringTokenSize(leftoverStartIndex, leftoverBytesCount);
            int requiredCapacity = Math.Max(pendingTokenSize, leftoverBytesCount);

            if (requiredCapacity >= _buffer.Length)
            {
                // Either the pending token is known to exceed the current buffer, or the unread
                // data fills it entirely; grow so the next stream read can make progress.
                var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(requiredCapacity, _buffer.Length * 2));
                Buffer.BlockCopy(_buffer, leftoverStartIndex, newBuffer, 0, leftoverBytesCount);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = newBuffer;
            }
            else if (leftoverBytesCount > 0)
            {
                Buffer.BlockCopy(_buffer, leftoverStartIndex, _buffer, 0, leftoverBytesCount);
            }

            int bytesReadFromStream = Stream.Read(_buffer, leftoverBytesCount, _buffer.Length - leftoverBytesCount);

            // Stream.Read returning 0 means end of stream; the data we have is the final block.
            _isFinalBlock = bytesReadFromStream == 0;
            _currentChunkSize = leftoverBytesCount + bytesReadFromStream;

            var newMemorySlice = new ReadOnlyMemory<byte>(_buffer, 0, _currentChunkSize);
            Reader.SlideData(newMemorySlice, _isFinalBlock);

            _logger.DebugFormat("Buffer refilled: read {0} byte(s), total in buffer now: {1}.", bytesReadFromStream, _currentChunkSize);
        }

        /// <summary>
        /// Returns the total encoded size (header + contents) of the next data item when it is a
        /// definite-length text or byte string whose length header is already buffered, or 0 when
        /// the size cannot be determined (other item types, indefinite-length strings, a not yet
        /// fully buffered header, or a declared length too large for a single buffer).
        ///
        /// This peeks at the CBOR string header directly because <see cref="CborReader"/> reports
        /// only that more data is needed, not how much. <see cref="RefillBuffer"/> is always
        /// entered at a data item boundary (<see cref="CborReader.PeekState"/> does not advance the
        /// reader and <see cref="CborReader.TrySkipValue"/> rolls back on failure), so the unread
        /// bytes start with an item's initial byte.
        /// </summary>
        private int GetPendingDefiniteLengthStringTokenSize(int unreadStartIndex, int unreadBytesCount)
        {
            const int MajorTypeByteString = 2;
            const int MajorTypeTextString = 3;

            if (unreadBytesCount == 0)
                return 0;

            byte initialByte = _buffer[unreadStartIndex];
            int majorType = initialByte >> 5;
            if (majorType != MajorTypeByteString && majorType != MajorTypeTextString)
                return 0;

            int additionalInfo = initialByte & 0b0001_1111;

            int headerSize;
            ulong declaredLength;
            var argument = _buffer.AsSpan(unreadStartIndex + 1, Math.Max(unreadBytesCount - 1, 0));

            if (additionalInfo < 24)
            {
                headerSize = 1;
                declaredLength = (ulong)additionalInfo;
            }
            else if (additionalInfo == 24 && argument.Length >= sizeof(byte))
            {
                headerSize = 1 + sizeof(byte);
                declaredLength = argument[0];
            }
            else if (additionalInfo == 25 && argument.Length >= sizeof(ushort))
            {
                headerSize = 1 + sizeof(ushort);
                declaredLength = BinaryPrimitives.ReadUInt16BigEndian(argument);
            }
            else if (additionalInfo == 26 && argument.Length >= sizeof(uint))
            {
                headerSize = 1 + sizeof(uint);
                declaredLength = BinaryPrimitives.ReadUInt32BigEndian(argument);
            }
            else if (additionalInfo == 27 && argument.Length >= sizeof(ulong))
            {
                headerSize = 1 + sizeof(ulong);
                declaredLength = BinaryPrimitives.ReadUInt64BigEndian(argument);
            }
            else
            {
                // Indefinite-length string, reserved encoding, or the length header itself is not
                // fully buffered yet; fall back to the default growth strategy.
                return 0;
            }

            if (declaredLength > (ulong)(int.MaxValue - headerSize))
            {
                // Can never fit a single buffer; the reader reports this as malformed content.
                return 0;
            }

            return headerSize + (int)declaredLength;
        }

        /// <summary>
        /// Adds a new segment to the current path stack.
        /// Typically used when entering a new object property.
        /// </summary>
        /// <param name="segment">The segment name to add</param>
        public void AddPathSegment(string segment)
        {
            _pathStack.Push(segment);
        }

        /// <summary>
        /// Removes the most recent segment from the path stack.
        /// Typically used when exiting a property.
        /// </summary>
        /// <returns>The segment that was removed.</returns>
        public string PopPathSegment()
        {
            return _pathStack.Pop();
        }

        protected override void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing && _buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                    Reader = null;
                }
                disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
