# Site Search API

All endpoints use the existing `/api` listener and API authentication. With a
password configured, send `X-WeaveFXP-API-Key: <password>`.

## Start

`POST /api/searches` returns HTTP 202 with an `id`, status and the first result page.

The default is native server search, not a client-side crawl:

```json
{
  "sites": ["SITE_A", "SITE_B"],
  "include": "Release.Name",
  "recursive": false
}
```

This sends `SITE SEARCH Release.Name` once to each selected site and processes its
directory results. Multiple plain queries can be separated with semicolons (up to
20). Query matching and server result limits are controlled by the server. Absolute
directory paths with optional glFTPd/WeaveFTPD `(Files/Megs/Age)` suffixes are
supported; unknown responses and unsupported commands produce errors, never an
automatic recursive fallback. Returned paths are filtered by `paths` and wildcard
`exclude` expressions. Native search does not invent size/date metadata.

For wildcard/regex, file, case-sensitive, date or size filtering, explicitly enable
`recursive: true` (the UI's **Recursive folder scan** checkbox):

```json
{
  "sites": ["SITE_A", "SITE_B"],
  "recursive": true,
  "paths": ["/SECTION"],
  "include": "*.sfv;*.nfo",
  "exclude": "*sample*",
  "regex": false,
  "case_sensitive": false,
  "kind": "both",
  "max_depth": 4,
  "max_results": 1000,
  "date_from": null,
  "date_to": null,
  "not_older_than_days": null,
  "min_bytes": null,
  "max_bytes": null
}
```

- `sites` and `paths`: 1-20 each; remote paths must be absolute.
- Include and exclude expressions match names, separated by semicolons (OR).
  Wildcards support `*` and `?`. Empty include matches everything. Regex mode
  accepts expressions such as `[._-](GERMAN|FRENCH)[._-]`; semicolons still separate
  expressions. Matching is case-insensitive unless requested otherwise.
- Excluded folders are not traversed. Unmatched parent folders are traversed.
- `kind`: `file`, `dir`, or `both`. Symlinks are neither followed nor returned.
- In recursive mode, depth 0 lists only the selected roots; maximum depth is 32.
- Date endpoints include the entire stated day. FTP LIST dates are interpreted as
  reported by the server; entries with unknown dates do not match date filters.
- Size filters apply to files only, in bytes. The UI uses MiB.
- At most 10,000 results, 20,000 discovered directories, four simultaneous
  searches and 15 minutes per search. Regex execution has a 100ms per-match limit.
- Search borrows listing connections from the existing shared site login pool.

## Read, Stop and Export

`GET /api/searches/{id}?offset=0&limit=100` returns progress, total matches,
results and per-directory errors. Results have stable integer `id` values.
Native mode reports `commands_completed` and `commands_pending`; recursive mode
reports `directories_scanned` and `pending_directories`. Pending counts are zero
once the search stops. A recursive scan collects all matching entries until it
finishes, is stopped or hits a limit; the first match does not end the scan.

Statuses: `running`, `completed`, `partial` (unreadable directories), `limited`
(result/directory cap), `cancelled` (user stop or time limit), `failed`.
Partial results remain available after stopping or encountering an error.

`POST /api/searches/{id}/cancel` stops the search, including an active listing.

`GET /api/searches/{id}/export` downloads the request, progress and all collected
results as JSON. Up to 20 searches are retained in memory; restarting the app
clears them. Export is a result snapshot, not a resumable crawler checkpoint.
The UI URL `/search?id={id}` reopens a retained search.

## Queue Results

`POST /api/searches/{id}/queue` returns the created manual transfer jobs:

```json
{
  "result_ids": [0, 3],
  "destination_site": "local",
  "destination_path": "D:\\Downloads"
}
```

Use a configured destination site and an absolute remote folder for FXP. An empty
local destination uses the configured download folder. Each result's name is
appended to the destination. Selected descendants of a selected folder are
omitted, avoiding duplicate transfers. Conflicting destination names are rejected.
Select 1-500 results per request. Searching never automatically starts transfers.

## Manual Queue

`POST /api/jobs/{id}/move/-1` moves a pending manual job up one position;
`POST /api/jobs/{id}/move/1` moves it down. Active jobs cannot move. Unavailable
moves return HTTP 409. Local jobs retain one active job per site; manual FXP jobs
retain the configured global and spread batch concurrency limits.

`DELETE /api/jobs/manual` removes current manual queue jobs, cancelling active and
pending transfers first. It leaves races and transferred files intact. It does
not purge unrelated archived history.

Reqfiller scripts, automatic request filling, pattern macros and resumable
import/checkpoint support are not part of this implementation.
