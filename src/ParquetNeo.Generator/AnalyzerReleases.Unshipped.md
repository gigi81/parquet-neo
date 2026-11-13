; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PNEO001 | ParquetNeo | Warning | A type marked [ParquetSerializable] has no generated mapping: it keeps working through reflection.
PNEO002 | ParquetNeo | Warning | Rows of a type can be written through a generated mapping, but not read.
