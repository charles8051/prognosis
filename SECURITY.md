# Security policy

## Supported versions

Security fixes land on the latest released major line. Older majors are not
patched.

| Version | Supported |
|---|---|
| 8.x | yes |
| < 8.0 | no |

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it through GitHub's private vulnerability reporting on this repository:
**Security → Report a vulnerability**. That channel is private to the maintainer
until a fix is published.

Useful things to include, if you have them: the affected version, a description
of the impact, and the smallest reproduction you can manage.

## What to expect

This is a single-maintainer project, so treat these as intentions rather than
guarantees: an acknowledgement within a week, an assessment of whether the report
is a vulnerability shortly after, and a fix released on the supported line once
one exists. You will be credited in the release notes unless you would rather not
be.

## Scope

Prognosis is a library. It computes health verdicts from values its host supplies;
it opens no sockets, reads no files, spawns no processes, and reads no clock other
than a monotonic timestamp. Reports of the following are in scope:

- a way to make the library crash, hang, deadlock, or consume unbounded memory
  from ordinary API use;
- a data race or lost update in the concurrent evaluation path;
- untrusted input reaching a dangerous sink through the serialization surface.

Out of scope: vulnerabilities in a host application's own health probes, and
findings that require an already-compromised process.
