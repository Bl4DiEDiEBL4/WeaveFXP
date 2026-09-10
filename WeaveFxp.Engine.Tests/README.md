# Engine Regression Checks

Run from the repository root:

```powershell
dotnet run --project WeaveFxp.Engine.Tests/WeaveFxp.Engine.Tests.csproj
```

This executable test runner exits nonzero on failure. It uses loopback FTP fixtures,
never configured production sites. Integration state is written beneath the system
temporary directory in a unique `weave-mesh-tests-*` folder.

Coverage includes asymmetric worker capacity, physical login and transfer limits,
sharing between races, cancellation, learned server limits, cached candidates,
WeaveFTPD 553/X-DUPE collisions, fresh-listing completion, fallback to a second
source, and permission refusals that must not count as destination coverage.
Global scoreboard checks cover cross-race priority, equal-score rotation, independent
site pairs, shared destination claims, pause/resume and registration cleanup.
Pool checks also cover paired slot reservation rollback, cancellation before login
and retaining permits until an opened client is returned.

These are correctness tests, not a throughput comparison with cbftp. Such a
comparison needs identical sites, slot limits, files and competing traffic.

Manual queue checks cover both move directions, FIFO dispatch, site and FXP batch
limits, cancellation, bulk removal and the actual FTP RETR order after reordering.
Site search checks cover wildcard/regex, exclusions, case, date/size/kind filters,
recursive traversal, depth and result caps, partial errors, pagination and stopping
an in-progress FTP listing. All fixtures are loopback-only.
Native SITE SEARCH checks verify the default mode, protocol parsing, root filters,
one command without directory listings, unsupported servers without fallback,
query validation, and command cancellation.
