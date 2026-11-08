using System.Diagnostics.CodeAnalysis;

namespace ParquetNeo.Exceptions;

/// <summary>A null where the schema says there is always a value; the serializer turns it into an <see cref="ArgumentException"/> that names the row.</summary>
[SuppressMessage("Major Code Smell", "S3871:Exception types should be \"public\"",
    Justification = "It is never seen by callers: it carries a null out of the shredders to the serializer, which rethrows it as an ArgumentException. Making it public would add an API that is never thrown.")]
internal sealed class MissingRequiredValueException(string description) : Exception(description)
{
}
