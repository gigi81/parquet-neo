using System.Buffers;

namespace ParquetNeo.Core;

/// <summary>Writes values PLAIN encoded; the shape of the <c>PlainEncoder.Write*s</c> methods.</summary>
internal delegate void PlainWriter<T>(ReadOnlySpan<T> values, IBufferWriter<byte> output);
