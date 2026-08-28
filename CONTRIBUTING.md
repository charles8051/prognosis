# Contributing

Thanks for considering it. This is a small library with a deliberate design, so
the most useful thing you can do before writing code is open an issue describing
the problem you hit.

## Before a pull request

**Read the relevant ADR.** `docs/adr/` records the decisions this library has
already made and, more importantly, the alternatives it rejected and why. Several
plausible changes have been considered and turned down there. If your change
diverges from an accepted ADR, say so explicitly in the pull request — that is a
conversation worth having, not a blocker, but it should be deliberate.

`docs/architecture.md` is the map, and it links every ADR.

## Building and testing

```bash
dotnet build Prognosis.slnx -c Release
dotnet test Prognosis.slnx -c Release
```

The .NET 10 SDK is required — `.slnx` needs 9.0.200 or newer to parse — even
though the shipped libraries target `netstandard2.0` and `netstandard2.1`. Keep
those target frameworks: the library is consumed from framework versions older
than the SDK that builds it.

## What a good change looks like

- **A test that fails before and passes after.** The suite is the specification;
  several ADR guarantees exist only as pinned tests.
- **No new public surface without a reason.** Additive API is cheap to ship and
  expensive to remove.
- **No clock, no IO, no scheduling in the core.** Time enters as a value from an
  injectable monotonic source; the library schedules nothing and starts no
  timers. This is ADR-010 and ADR-011, and it is the constraint most likely to
  be violated by accident.
- **Match the surrounding style.** There is no formatter config; follow the file
  you are editing.

## Commit messages

Conventional-commit prefixes (`feat:`, `fix:`, `docs:`, `chore:`, `test:`,
`ci:`), and a body explaining *why* when the reason is not obvious from the diff.

## Security

Do not open a public issue for a security problem — see [SECURITY.md](SECURITY.md).
