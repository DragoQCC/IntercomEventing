; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
INTEV001 | IntercomEventing | Error | Type containing an [IntercomEvent] field must be partial
INTEV002 | IntercomEventing | Error | [IntercomEvent] field is declared in an unsupported type
INTEV003 | IntercomEventing | Error | [IntercomEvent] field type is not an IntercomEvent kind
INTEV004 | IntercomEventing | Error | [IntercomEvent] field type must not be nullable
INTEV005 | IntercomEventing | Warning | [IntercomEvent] field should be readonly
INTEV006 | IntercomEventing | Error | Invalid [IntercomEvent] event name
INTEV007 | IntercomEventing | Error | Generated [IntercomEvent] member conflicts with an existing name
INTEV008 | IntercomEventing | Error | Two [IntercomEvent] fields generate the same member
INTEV009 | IntercomEventing | Error | [IntercomEvent] call type is less accessible than the generated members
INTEV010 | IntercomEventing | Error | [IntercomEvent] field must not be const
INTEV011 | IntercomEventing | Error | [IntercomEvent] must be applied to a declared field
INTEV012 | IntercomEventing | Error | [IntercomEvent] call type comes from a reference that has only an extern alias
