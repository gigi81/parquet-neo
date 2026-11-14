namespace ParquetNeo.Tests.Core;

/// <summary>
/// A stream to write a file to that behaves like a slow or faulty one: each write takes a while, one can be held until the test lets
/// it go, one can fail, and how many were going at once is counted. What a write wrote is read from its memory when the write ends,
/// as a real stream does, so that memory given back to a pool too early shows in the bytes.
/// </summary>
internal sealed class TestDestination : Stream
{
    private readonly MemoryStream _captured = new();
    private readonly List<TaskCompletionSource> _gates = [];
    private int _inFlight;
    private int _started;

    /// <summary>How long every write takes, before it is let go.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <summary>The number (from 0) of a write that throws when it ends, or -1.</summary>
    public int FailOnWrite { get; init; } = -1;

    /// <summary>The writes (from 0) that wait until <see cref="Release"/> is called for them.</summary>
    public HashSet<int> HeldWrites { get; init; } = [];

    public int MaxInFlight { get; private set; }

    public int InFlight => Volatile.Read(ref _inFlight);

    public int StartedWrites => Volatile.Read(ref _started);

    public int CompletedWrites { get; private set; }

    public byte[] Bytes => _captured.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _captured.Length;

    public override long Position
    {
        get => _captured.Length;
        set => throw new NotSupportedException();
    }

    /// <summary>Lets a held write go.</summary>
    public void Release(int write)
    {
        lock (_gates)
        {
            while (_gates.Count <= write)
            {
                _gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            _gates[write].TrySetResult();
        }
    }

    /// <summary>Waits until this many writes have started.</summary>
    public async Task WaitForStartedAsync(int writes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (StartedWrites < writes)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {StartedWrites} of {writes} writes started.");
            }

            await Task.Delay(1);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int index = Interlocked.Increment(ref _started) - 1;
        int now = Interlocked.Increment(ref _inFlight);
        MaxInFlight = Math.Max(MaxInFlight, now);
        try
        {
            if (HeldWrites.Contains(index))
            {
                Task gate;
                lock (_gates)
                {
                    while (_gates.Count <= index)
                    {
                        _gates.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    }

                    gate = _gates[index].Task;
                }

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            if (index == FailOnWrite)
            {
                throw new IOException($"write {index} failed");
            }

            _captured.Write(buffer.Span);
            CompletedWrites++;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => _captured.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _captured.Write(buffer);

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
