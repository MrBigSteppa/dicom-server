﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Internal;

namespace Microsoft.Health.Dicom.Api.Web;

/// <summary>
/// A Stream that wraps another stream and enables rewinding by buffering the content as it is read.
/// The content is buffered in memory up to a certain size and then spooled to a temp file on disk.
/// The temp file will be deleted on Dispose.
/// </summary>
public class FileBufferingReadStream : Stream
{
    private const int _maxRentedBufferSize = 1024 * 1024; // 1MB
    private readonly Stream _inner;
    private readonly ArrayPool<byte> _bytePool;
    private readonly int _memoryThreshold;
    private readonly long? _bufferLimit;
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    private string? _tempFileDirectory;
#pragma warning restore CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    private readonly Func<string>? _tempFileDirectoryAccessor;
#pragma warning restore CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    private string? _tempFileName;
#pragma warning restore CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.

    private Stream _buffer;
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    private byte[]? _rentedBuffer;
#pragma warning restore CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    private bool _inMemory = true;
    private bool _completelyBuffered;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="FileBufferingReadStream" />.
    /// </summary>
    /// <param name="inner">The wrapping <see cref="Stream" />.</param>
    /// <param name="memoryThreshold">The maximum size to buffer in memory.</param>
    public FileBufferingReadStream(Stream inner, int memoryThreshold)
        : this(inner, memoryThreshold, bufferLimit: null, tempFileDirectoryAccessor: AspNetCoreTempDirectory.TempDirectoryFactory)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FileBufferingReadStream" />.
    /// </summary>
    /// <param name="inner">The wrapping <see cref="Stream" />.</param>
    /// <param name="memoryThreshold">The maximum size to buffer in memory.</param>
    /// <param name="bufferLimit">The maximum size that will be buffered before this <see cref="Stream"/> throws.</param>
    /// <param name="tempFileDirectoryAccessor">Provides the temporary directory to which files are buffered to.</param>
    public FileBufferingReadStream(
        Stream inner,
        int memoryThreshold,
        long? bufferLimit,
        Func<string> tempFileDirectoryAccessor)
        : this(inner, memoryThreshold, bufferLimit, tempFileDirectoryAccessor, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FileBufferingReadStream" />.
    /// </summary>
    /// <param name="inner">The wrapping <see cref="Stream" />.</param>
    /// <param name="memoryThreshold">The maximum size to buffer in memory.</param>
    /// <param name="bufferLimit">The maximum size that will be buffered before this <see cref="Stream"/> throws.</param>
    /// <param name="tempFileDirectoryAccessor">Provides the temporary directory to which files are buffered to.</param>
    /// <param name="bytePool">The <see cref="ArrayPool{T}"/> to use.</param>
    public FileBufferingReadStream(
        Stream inner,
        int memoryThreshold,
        long? bufferLimit,
        Func<string> tempFileDirectoryAccessor,
        ArrayPool<byte> bytePool)
    {
        if (inner == null)
        {
            throw new ArgumentNullException(nameof(inner));
        }

        if (tempFileDirectoryAccessor == null)
        {
            throw new ArgumentNullException(nameof(tempFileDirectoryAccessor));
        }

        _bytePool = bytePool;
        if (memoryThreshold <= _maxRentedBufferSize)
        {
            _rentedBuffer = bytePool.Rent(memoryThreshold);
            _buffer = new MemoryStream(_rentedBuffer);
            _buffer.SetLength(0);
        }
        else
        {
            _buffer = new MemoryStream();
        }

        _inner = inner;
        _memoryThreshold = memoryThreshold;
        _bufferLimit = bufferLimit;
        _tempFileDirectoryAccessor = tempFileDirectoryAccessor;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FileBufferingReadStream" />.
    /// </summary>
    /// <param name="inner">The wrapping <see cref="Stream" />.</param>
    /// <param name="memoryThreshold">The maximum size to buffer in memory.</param>
    /// <param name="bufferLimit">The maximum size that will be buffered before this <see cref="Stream"/> throws.</param>
    /// <param name="tempFileDirectory">The temporary directory to which files are buffered to.</param>
    public FileBufferingReadStream(
        Stream inner,
        int memoryThreshold,
        long? bufferLimit,
        string tempFileDirectory)
        : this(inner, memoryThreshold, bufferLimit, tempFileDirectory, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="FileBufferingReadStream" />.
    /// </summary>
    /// <param name="inner">The wrapping <see cref="Stream" />.</param>
    /// <param name="memoryThreshold">The maximum size to buffer in memory.</param>
    /// <param name="bufferLimit">The maximum size that will be buffered before this <see cref="Stream"/> throws.</param>
    /// <param name="tempFileDirectory">The temporary directory to which files are buffered to.</param>
    /// <param name="bytePool">The <see cref="ArrayPool{T}"/> to use.</param>
    public FileBufferingReadStream(
        Stream inner,
        int memoryThreshold,
        long? bufferLimit,
        string tempFileDirectory,
        ArrayPool<byte> bytePool)
    {
        if (inner == null)
        {
            throw new ArgumentNullException(nameof(inner));
        }

        if (tempFileDirectory == null)
        {
            throw new ArgumentNullException(nameof(tempFileDirectory));
        }

        _bytePool = bytePool;
        if (memoryThreshold <= _maxRentedBufferSize)
        {
            _rentedBuffer = bytePool.Rent(memoryThreshold);
            _buffer = new MemoryStream(_rentedBuffer);
            _buffer.SetLength(0);
        }
        else
        {
            _buffer = new MemoryStream();
        }

        _inner = inner;
        _memoryThreshold = memoryThreshold;
        _bufferLimit = bufferLimit;
        _tempFileDirectory = tempFileDirectory;
    }

    /// <summary>
    /// The maximum amount of memory in bytes to allocate before switching to a file on disk.
    /// </summary>
    /// <remarks>
    /// Defaults to 32kb.
    /// </remarks>
    public int MemoryThreshold => _memoryThreshold;

    /// <summary>
    /// Gets a value that determines if the contents are buffered entirely in memory.
    /// </summary>
    public bool InMemory
    {
        get { return _inMemory; }
    }

    /// <summary>
    /// Gets a value that determines where the contents are buffered on disk.
    /// </summary>
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    public string? TempFileName
#pragma warning restore CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
    {
        get { return _tempFileName; }
    }

    /// <inheritdoc/>
    public override bool CanRead
    {
        get { return !_disposed; }
    }

    /// <inheritdoc/>
    public override bool CanSeek
    {
        get { return !_disposed; }
    }

    /// <inheritdoc/>
    public override bool CanWrite
    {
        get { return false; }
    }

    /// <summary>
    /// The total bytes read from and buffered by the stream so far, it will not represent the full
    /// data length until the stream is fully buffered. e.g. using <c>stream.DrainAsync()</c>.
    /// </summary>
    public override long Length
    {
        get { return _buffer.Length; }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get { return _buffer.Position; }
        // Note this will not allow seeking forward beyond the end of the buffer.
        set
        {
            ThrowIfDisposed();
            _buffer.Position = value;
        }
    }

    /// <summary>
    /// Specifies the IO buffer size used for the FileStream buffer.
    /// </summary>
    public int FileIOBufferSize { get; set; } = 16 * 1024;

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        if (!_completelyBuffered && origin == SeekOrigin.End)
        {
            // Can't seek from the end until we've finished consuming the inner stream
            throw new NotSupportedException("The content has not been fully buffered yet.");
        }
        else if (!_completelyBuffered && origin == SeekOrigin.Current && offset + Position > Length)
        {
            // Can't seek past the end of the buffer until we've finished consuming the inner stream
            throw new NotSupportedException("The content has not been fully buffered yet.");
        }
        else if (!_completelyBuffered && origin == SeekOrigin.Begin && offset > Length)
        {
            // Can't seek past the end of the buffer until we've finished consuming the inner stream
            throw new NotSupportedException("The content has not been fully buffered yet.");
        }
        return _buffer.Seek(offset, origin);
    }

    private Stream CreateTempFile()
    {
        if (_tempFileDirectory == null)
        {
            Debug.Assert(_tempFileDirectoryAccessor != null);
            _tempFileDirectory = _tempFileDirectoryAccessor();
            Debug.Assert(_tempFileDirectory != null);
        }

        _tempFileName = Path.Combine(_tempFileDirectory, "ASPNETCORE_" + Guid.NewGuid().ToString() + ".tmp");
        return new FileStream(_tempFileName, FileMode.Create, FileAccess.ReadWrite, FileShare.Delete, FileIOBufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        if (_buffer.Position < _buffer.Length || _completelyBuffered)
        {
            // Just read from the buffer
            return _buffer.Read(buffer);
        }

        var read = _inner.Read(buffer);

        if (_bufferLimit.HasValue && _bufferLimit - read < _buffer.Length)
        {
            throw new IOException("Buffer limit exceeded.");
        }

        // We're about to go over the threshold, switch to a file
        if (_inMemory && _memoryThreshold - read < _buffer.Length)
        {
            _inMemory = false;
            var oldBuffer = _buffer;
            _buffer = CreateTempFile();
            if (_rentedBuffer == null)
            {
                // Copy data from the in memory buffer to the file stream using a pooled buffer
                oldBuffer.Position = 0;
                var rentedBuffer = _bytePool.Rent(Math.Min((int)oldBuffer.Length, _maxRentedBufferSize));
                try
                {
                    var copyRead = oldBuffer.Read(rentedBuffer);
                    while (copyRead > 0)
                    {
                        _buffer.Write(rentedBuffer.AsSpan(0, copyRead));
                        copyRead = oldBuffer.Read(rentedBuffer);
                    }
                }
                finally
                {
                    _bytePool.Return(rentedBuffer);
                }
            }
            else
            {
                _buffer.Write(_rentedBuffer.AsSpan(0, (int)oldBuffer.Length));
                _bytePool.Return(_rentedBuffer);
                _rentedBuffer = null;
            }
        }

        if (read > 0)
        {
            _buffer.Write(buffer.Slice(0, read));
        }
        // Allow zero-byte reads
        else if (buffer.Length > 0)
        {
            _completelyBuffered = true;
        }

        return read;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc/>
    [SuppressMessage("ApiDesign", "RS0027:Public API with optional parameter(s) should have the most parameters amongst its public overloads.", Justification = "Required to maintain compatibility")]
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_buffer.Position < _buffer.Length || _completelyBuffered)
        {
            // Just read from the buffer
            return await _buffer.ReadAsync(buffer, cancellationToken);
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken);

        if (_bufferLimit.HasValue && _bufferLimit - read < _buffer.Length)
        {
            throw new IOException("Buffer limit exceeded.");
        }

        await StoreAsync(buffer[..read], cancellationToken);

        // Allow zero-byte reads
        if (read == 0 && buffer.Length > 0)
        {
            _completelyBuffered = true;
        }

        return read;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        // Set a minimum buffer size of 4K since the base Stream implementation has weird behavior when the stream is
        // seekable *and* the length is 0 (it passes in a buffer size of 1).
        // See https://github.com/dotnet/runtime/blob/222415c56c9ea73530444768c0e68413eb374f5d/src/libraries/System.Private.CoreLib/src/System/IO/Stream.cs#L164-L184
        bufferSize = Math.Max(4096, bufferSize);

        // If we're completed buffered then copy from the underlying source
        if (_completelyBuffered)
        {
            return _buffer.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        async Task CopyToAsyncImpl()
        {
            // At least a 4K buffer
            byte[] buffer = _bytePool.Rent(bufferSize);
            try
            {
                while (true)
                {
                    int bytesRead = await ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }
            }
            finally
            {
                _bytePool.Return(buffer);
            }
        }

        return CopyToAsyncImpl();
    }

    /// <summary>
    /// Reads this stream into the buffer.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    public async Task BufferAsync(CancellationToken cancellationToken = default)
    {
        if (_completelyBuffered)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var buffer = _bytePool.Rent(_maxRentedBufferSize);
        long total = 0;
        try
        {
            var read = 0;
            do
            {
                // Fill a segment
                var segmentSize = 0;
                do
                {
                    read = await _inner.ReadAsync(buffer.AsMemory(segmentSize), cancellationToken);
                    segmentSize += read;
                    // Not all streams support cancellation directly.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_bufferLimit.HasValue && _bufferLimit.Value - total < read)
                    {
                        throw new InvalidDataException($"The stream exceeded the buffer limit {_bufferLimit.Value}.");
                    }
                    total += read;
                }
                while (read > 0 && segmentSize < buffer.Length);

                await StoreAsync(buffer.AsMemory(0, segmentSize), cancellationToken);
            }
            while (read > 0);

            _completelyBuffered = true;
        }
        finally
        {
            _bytePool.Return(buffer);
        }
    }

    private async Task StoreAsync(Memory<byte> memory, CancellationToken cancellationToken)
    {
        if (memory.Length == 0)
        {
            return;
        }

        // Do we need to offload to disk?
        if (_inMemory && _memoryThreshold - memory.Length < _buffer.Length)
        {
            _inMemory = false;
            var oldBuffer = _buffer;
            _buffer = CreateTempFile();
            if (_rentedBuffer == null)
            {
                oldBuffer.Position = 0;
                var rentedBuffer = _bytePool.Rent(Math.Min((int)oldBuffer.Length, _maxRentedBufferSize));
                try
                {
                    // oldBuffer is a MemoryStream, no need to do async reads.
                    var copyRead = oldBuffer.Read(rentedBuffer);
                    while (copyRead > 0)
                    {
                        await _buffer.WriteAsync(rentedBuffer.AsMemory(0, copyRead), cancellationToken);
                        copyRead = oldBuffer.Read(rentedBuffer);
                    }
                }
                finally
                {
                    _bytePool.Return(rentedBuffer);
                }
            }
            else
            {
                await _buffer.WriteAsync(_rentedBuffer.AsMemory(0, (int)oldBuffer.Length), cancellationToken);
                _bytePool.Return(_rentedBuffer);
                _rentedBuffer = null;
            }
        }

        await _buffer.WriteAsync(memory, cancellationToken);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_rentedBuffer != null)
            {
                _bytePool.Return(_rentedBuffer);
            }

            if (disposing)
            {
                _buffer.Dispose();
            }
        }
    }

    /// <inheritdoc/>
#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
    public override async ValueTask DisposeAsync()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_rentedBuffer != null)
            {
                _bytePool.Return(_rentedBuffer);
            }

            await _buffer.DisposeAsync();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileBufferingReadStream));
        }
    }
}

public static class AspNetCoreTempDirectory
{
    private static string s_tempDirectory;

    public static Func<string> TempDirectoryFactory => GetTempDirectory;

    private static string GetTempDirectory()
    {
        if (s_tempDirectory == null)
        {
            // Look for folders in the following order.
            // ASPNETCORE_TEMP - User set temporary location.
            string temp = Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath();

            if (!Directory.Exists(temp))
            {
                throw new DirectoryNotFoundException(temp);
            }

            s_tempDirectory = temp;
        }

        return s_tempDirectory;
    }
}
