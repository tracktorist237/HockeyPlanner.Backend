# Tech Debt Entry Template

Add the entry to the appropriate category in `tech-debt-registry.md`.

| Field | Value |
|---|---|
| ID | `SEC/ARC/TECH/INF/PERF-NNN` |
| Description | Confirmed behavior, not a proposed solution |
| Evidence | Exact repository path and method; use the class or configuration file when no specific method applies |
| Priority | P0 / P1 / P2 / P3 |
| Impact | Security, data, reliability, maintainability or latency impact |
| Milestone / notes | Existing milestone or `Deferred`, plus any revisit note |
| Status | Open / Planned / In Progress / Blocked / Accepted / Resolved |
| Issue/PR | Link when available |
| Resolution evidence | Tests, commit or ADR when resolved |

Rules:

- Priority mapping is fixed: `P0` = critical, `P1` = high, `P2` = medium and
  `P3` = low.
- Do not add hypothetical debt without code/config evidence.
- Do not create a new milestone from an entry.
- An accepted Constitution exception requires an ADR.
- Resolve an entry only after verification is merged.
