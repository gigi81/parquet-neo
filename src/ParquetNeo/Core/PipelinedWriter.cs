namespace ParquetNeo.Core;

/// <summary>
/// Writes the bytes a writer encoded to a stream without making the writer wait: a write is started and the next row group is
/// encoded while it is on its way; the write before is awaited only when the next is due, since a stream takes one write at a time.
/// So the time to write a file is that of the slower of encoding and writing, instead of both added.
/// </summary>
internal sealed class PipelinedWriter(Stream destination, CancellationToken cancellationToken) : IAsyncDisposable
{
    private Task _pending = Task.CompletedTask;
    private StagedBytes? _inFlight;

    /// <summary>
    /// Waits for the write before, if one is still going, and starts writing <paramref name="bytes"/>, which this takes over and
    /// disposes once they are written (also when the write before failed, or this is cancelled).
    /// </summary>
    public async ValueTask WriteAsync(StagedBytes bytes)
    {
        try
        {
            await FinishPendingAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            bytes.Dispose();
            throw;
        }

        _inFlight = bytes;
        _pending = destination.WriteAsync(bytes.Memory, cancellationToken).AsTask();
    }

    /// <summary>Waits for the last write.</summary>
    public ValueTask CompleteAsync() => FinishPendingAsync();

    /// <summary>Waits for the write that is going, without reporting how it ended, so that its bytes are not given back to the pool while the stream still reads them.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _pending.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Whatever went wrong was, or will be, reported by the call that is in progress.
        }

        _inFlight?.Dispose();
        _inFlight = null;
    }

    private async ValueTask FinishPendingAsync()
    {
        var pending = _pending;
        _pending = Task.CompletedTask;
        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            _inFlight?.Dispose();
            _inFlight = null;
        }
    }
}
