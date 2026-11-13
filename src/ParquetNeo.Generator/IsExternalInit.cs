using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init-only properties compile for netstandard2.0, which the compiler loads a generator as.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
