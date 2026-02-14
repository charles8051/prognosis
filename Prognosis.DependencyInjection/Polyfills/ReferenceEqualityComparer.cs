// Polyfill: ReferenceEqualityComparer is available in .NET 5+ but not in netstandard.

using System.Runtime.CompilerServices;

namespace System.Collections.Generic;

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    private ReferenceEqualityComparer() { }

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
}
