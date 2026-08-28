// Polyfill: ReferenceEqualityComparer is available in .NET 5+ but not in netstandard.
// Uses a project-scoped namespace to avoid CS0436 conflicts with the identical polyfill
// in the core Prognosis assembly (both target System.Collections.Generic otherwise).

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Prognosis.DependencyInjection.Polyfills;

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    private ReferenceEqualityComparer() { }

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
}
