# Logs MCP Improvement Implementation Plan

## Goal

Improve `Logs_MCP` so the log search experience is more precise, easier to use for first-pass investigation, and more scalable for large log folders.

The current routing model remains valid and should be preserved:

- `server_name` selects the mapped server group from `AppServerMap`
- `target_app` narrows the physical log folder(s)
- search and filtering are then applied within those folders

---

## Current State Summary

Today, the MCP tool:

- requires `server_name`
- requires `target_app`
- requires `search_keyword`
- filters by date range
- limits returned matching lines with `max_results`
- searches recursively in matching app folders
- returns raw matching lines with server and file prefixes

This works for direct string search, but it has several limitations:

- too much reliance on plain substring matching
- no simple “show recent errors” workflow
- weak diagnostics when nothing matches
- no summary-first response format
- no pagination model
- no discovery helper for valid app folders
- no expansion workflow for surrounding context

---

## Review of Suggested Improvements

## 1. Add structured filters alongside plain text search

### Recommendation
Implement.

### Reason
This is the highest-value improvement. It directly reduces noisy results and better supports JSON-based logs.

### Proposed fields
- `log_level`
- `correlation_id`
- `exception_type`
- `exact_match`
- `phrase`
- `contains`

### Notes
- `search_keyword` should no longer be the only way to search
- field-aware filters should be best-effort for plain-text logs
- structured matching should prefer parsed JSON fields when available

---

## 2. Add a no-keyword mode for “show recent errors”

### Recommendation
Implement.

### Reason
This is a common first diagnostic step and should not require inventing a text term.

### Proposed behavior
Allow searches when `search_keyword` is omitted, provided at least one of the following is present:

- `log_level`
- `exception_type`
- `correlation_id`
- `recent_errors = true`

### Example use cases
- recent errors for `potsplitter` on `finweb-uat1`
- recent warnings in the last 24 hours

---

## 3. Improve no-match guidance

### Recommendation
Implement.

### Reason
A no-match result should help the caller decide what to try next.

### Proposed distinctions in responses
- `server_name` mapping not found
- app folder not found
- app folder found but no candidate log files in date range
- log files found but no matching entries

### Proposed suggestions
- try common terms such as `Error`, `Exception`, `WARN`, `404`
- suggest widening the date range
- list close folder matches when app folder is not found
- mention whether files existed for the requested dates

---

## 4. Return a built-in summary before raw lines

### Recommendation
Implement.

### Reason
A summary-first response is easier to consume in chat than a raw list of lines.

### Proposed summary fields
- total matches
- matched servers
- matched files
- first timestamp seen
- last timestamp seen
- top repeated messages
- top exception types

### Proposed default response shape
1. summary block
2. first page of matching lines

---

## 5. Handle large results more cleanly

### Recommendation
Implement after summary support.

### Reason
A raw dump is difficult to use interactively.

### Proposed model
- `page_size`
- `offset`
- default: summary + first page

### Notes
This is preferable to writing large outputs to temporary files.

---

## 6. Support phrase and field-aware matching

### Recommendation
Implement.

### Reason
Substring search alone causes false positives.

### Proposed semantics
- `contains`: case-insensitive substring
- `phrase`: exact phrase match
- `exact_match`: exact field or whole-line comparison where practical

### Notes
This should be aligned with structured filters and not implemented as a separate inconsistent system.

---

## 7. Add app discovery or validation

### Recommendation
Implement as a separate MCP tool.

### Reason
Discoverability should not be overloaded into the main search tool.

### Proposed new tool
- `list_app_folders`

### Proposed parameters
- `server_name`
- optional `filter`
- optional `max_results`

### Proposed output
- matching folder names
- total count
- optional hints when no folders match

---

## 8. Add surrounding context retrieval

### Recommendation
Defer to a later phase.

### Reason
Useful, but it requires stable hit identity and a more explicit result model.

### Proposed future tool
- `get_log_context`

### Likely parameters
- `server`
- `file_path`
- `line_number`
- `before`
- `after`

---

## Proposed Delivery Phases

## Phase 1: Search Quality and Usability

### Scope
- make `search_keyword` optional
- add structured filters:
  - `log_level`
  - `correlation_id`
  - `exception_type`
- add search modes:
  - `contains`
  - `phrase`
  - `exact_match`
- add `recent_errors` support
- improve no-match diagnostics
- return summary + first page of hits

### Expected outcome
The tool becomes much more useful for day-to-day troubleshooting without changing the routing model.

---

## Phase 2: Discovery and Result Navigation

### Scope
- add `list_app_folders(server_name, filter?, max_results?)`
- add pagination parameters to `search_federated_logs`
  - `page_size`
  - `offset`
- improve folder-match suggestions

### Expected outcome
Users can find valid target apps more safely and browse large results more predictably.

---

## Phase 3: Context Expansion

### Scope
- add a context retrieval tool for surrounding lines
- define stable hit metadata in search results
- support expansion by file and line number or by correlation ID

### Expected outcome
Users can move from search results to deeper investigation without repeating searches manually.

---

## Proposed MCP Contract Evolution

## Current contract
- `server_name`
- `target_app`
- `search_keyword`
- `start_date`
- `end_date`
- `max_results`

## Proposed Phase 1 contract
- `server_name` (required)
- `target_app` (required)
- `search_keyword` (optional)
- `log_level` (optional)
- `correlation_id` (optional)
- `exception_type` (optional)
- `phrase` (optional)
- `exact_match` (optional, boolean)
- `recent_errors` (optional, boolean)
- `start_date` (optional)
- `end_date` (optional)
- `max_results` (optional)

### Validation rule
At least one content filter must be provided:

- `search_keyword`
- `log_level`
- `correlation_id`
- `exception_type`
- `recent_errors`

---

## Technical Design Notes

## Search pipeline
1. resolve servers from `server_name`
2. enumerate app folders matching `target_app`
3. enumerate candidate log files for date range
4. parse line as structured JSON where possible
5. apply structured filters
6. fall back to text matching where needed
7. collect summary metadata and page results

## Parsing strategy
- preserve the current streaming approach
- avoid loading full files into memory
- parse JSON lines opportunistically
- do not fail the entire search when a line is not valid JSON

## Result model direction
Introduce an internal result type to support summaries and future pagination/context retrieval.

Suggested internal models:
- `LogSearchRequest`
- `LogSearchHit`
- `LogSearchSummary`
- `LogSearchResponse`

---

## Testing Plan

## Phase 1 tests
- validation when all content filters are missing
- validation for `recent_errors` mode
- `log_level` filtering on JSON lines
- `correlation_id` filtering on JSON lines
- exact phrase matching behavior
- no-match diagnostics distinguish:
  - unknown `server_name`
  - missing app folder
  - no files in range
  - no matching entries
- summary block contains expected counts

## Phase 2 tests
- app discovery returns expected folders
- discovery filtering works
- paged responses are stable and deterministic

## Phase 3 tests
- context expansion returns surrounding lines correctly
- invalid file/line references return safe errors

---

## Recommended Implementation Order

1. introduce internal request/response models
2. add structured parsing and filter evaluation helpers
3. make `search_keyword` optional with content-filter validation
4. add summary generation
5. improve no-match diagnostics
6. add app discovery tool
7. add pagination
8. add context expansion tool

---

## Out of Scope for Now

The following should not be part of the first implementation wave:

- semantic message clustering
- cross-server deduplication heuristics
- indexing/caching infrastructure
- free-form query language
- heavy schema inference for every log format

---

## Success Criteria

The improvement effort is successful if:

- users can search by structured error fields instead of only substring text
- users can run a recent-errors check without a keyword
- no-match responses clearly explain what failed
- default output is summarized and easier to scan
- users can discover valid app folders safely
- future context expansion can be added without redesigning the core model
