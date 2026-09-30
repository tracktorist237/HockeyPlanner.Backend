# NuGet JSON v1 regression fixtures

`nuget-v1.json` is the .NET SDK 10.0.400 serialized output captured by the author
from the actual solution with `dotnet package list --project
HockeyPlanner.Backend.sln --no-restore --vulnerable --include-transitive --format
json --output-version 1`. Project paths were replaced by `/repo/<basename>` and
the sources list reduced to the public nuget.org URL. Package IDs, versions,
advisories, severity and clean-project omission of frameworks are unchanged.

`nuget-v1-clean.json` preserves the actual clean project records from that capture.
It is a parser fixture, not a claim that the entire solution has no advisories.
Negative tests add `problems` warning/error records and mutate schema types on
these serialized shapes, including an exit-0 incomplete audit through the wrapper.
The injected diagnostics contain synthetic prose only, no credentials or raw logs.
