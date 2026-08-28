# Third-party notices

Prognosis is licensed under the MIT License; see [LICENSE](LICENSE).

It contains a small amount of code derived from other projects, listed below. All
of it is MIT-licensed and compatible with this project's licence.

## .NET Platform (`dotnet/runtime`)

Copyright (c) .NET Foundation and Contributors
Licensed under the MIT License
<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>

The following files are polyfills taken from the .NET runtime so that
`netstandard2.0` / `netstandard2.1` targets can use language features that
otherwise require a newer framework. Each retains its original .NET Foundation
header.

- `Polyfills/IsExternalInit.cs`
- `Prognosis.DependencyInjection/Polyfills/IsExternalInit.cs`

`IsExternalInit` is the marker type the compiler requires for init-only
properties, and therefore for records, on targets older than .NET 5.

## Independently written polyfills

`Polyfills/ReferenceEqualityComparer.cs` and
`Prognosis.DependencyInjection/Polyfills/ReferenceEqualityComparer.cs` provide a
`netstandard2.0` stand-in for `System.Collections.Generic.ReferenceEqualityComparer`.
They are original to this project and are covered by [LICENSE](LICENSE); they are
noted here only because they sit alongside the vendored files above.
