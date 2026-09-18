namespace DotCov.Tests.Infrastructure;

public sealed class ScriptedReadStream(byte[] bytes, int chunkSize, bool asyncOnly = false) : Stream
{
    private int _position;
    private bool _disposed;
    private int? _failureAt;
    private IOException? _failure;
    private int? _pauseAt;
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Paused => _paused.Task;
    public bool SynchronousReadAttempted { get; private set; }

    public void PauseAt(int byteOffset) => _pauseAt = byteOffset;
    public void Resume() => _resumed.TrySetResult();

    public void FailAt(int byteOffset, IOException failure)
    {
        _failureAt = byteOffset;
        _failure = failure;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        SynchronousReadAttempted = true;
        if (asyncOnly) throw new NotSupportedException("This source requires asynchronous reads.");
        return ReadChunk(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pauseAt is { } pauseAt && _position >= pauseAt)
        {
            _paused.TrySetResult();
            await _resumed.Task.WaitAsync(cancellationToken);
        }
        return ReadChunk(buffer.Span);
    }

    private int ReadChunk(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failureAt is { } failureAt && _position >= failureAt) throw _failure!;
        var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - _position);
        if (_failureAt is { } fault) count = Math.Min(count, fault - _position);
        if (_pauseAt is { } pause && _position < pause) count = Math.Min(count, pause - _position);
        bytes.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        Resume();
        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
