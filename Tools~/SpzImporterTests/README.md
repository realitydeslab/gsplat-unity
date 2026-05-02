# SpzImporterTests

Standalone .NET console harness that exercises `SpzReader.cs` outside of Unity.
The folder is named with a trailing `~` so it is excluded from the UPM package
when end-users install via Unity Package Manager — it is repo-only.

## Run

```sh
csc -nologo -langversion:latest Program.cs ../../Editor/SpzReader.cs -out:run.exe
mono run.exe samples/hornedlizard.spz samples/racoonfamily.spz
```

`csc` is shipped with Mono on macOS (`/Library/Frameworks/Mono.framework`); on
Linux/Windows use the equivalent C# 8+ compiler.

Sample SPZ fixtures live in `github.com/nianticlabs/spz/tree/main/samples`
(MIT-licensed). Drop them into a local `samples/` folder beside `run.exe`.

## What it checks

- Round-trips both gzip-wrapped legacy (v1, v2, v3) headers without crashing.
- Reports per-file statistics: numPoints, SH degree, position bounding box,
  alpha distribution, color range, quaternion unit-length residual.
- Asserts: array lengths match `NumPoints`, no NaN/Inf in any output buffer,
  every unpacked quaternion is unit-length within 1%.

## Expected output (samples from nianticlabs/spz)

```
=== samples/hornedlizard.spz ===
  numPoints       : 786,233
  shDegree        : 3 (dim/channel = 15)
  ...
  non-unit quats  : 0 / 786233
  OK

=== samples/racoonfamily.spz ===
  numPoints       : 932,560
  shDegree        : 3 (dim/channel = 15)
  ...
  non-unit quats  : 0 / 932560
  OK

All files OK.
```
