using System;

namespace ParquetNeo.Generator
{
    /// <summary>Thrown when a type cannot have a generated mapping, with the reason, to be reported as a warning.</summary>
    public sealed class UnmappableTypeException : Exception
    {
        public UnmappableTypeException(string reason)
            : base(reason)
        {
        }
    }
}
