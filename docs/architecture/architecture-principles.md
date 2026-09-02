# Architecture Principles

Status: **Approved and mandatory**

These principles complement the
[Backend Constitution](backend-constitution.md); the Constitution wins if a
short principle can be interpreted in more than one way.

1. Preserve public behavior before moving code.
2. Derive identity from authentication, never from client input.
3. Authorize access to the stored resource, not to identifiers in a DTO.
4. Keep business invariants in application/domain code and protect critical
   invariants in the database where practical.
5. Keep controllers thin and transport-focused.
6. Make transaction boundaries explicit and keep external IO outside them.
7. Treat push and email as delivery mechanisms, not primary storage.
8. Use explicit DTOs and freeze API behavior with contract tests.
9. Prefer bounded queries and simple projections over large object graphs.
10. Make time, cancellation, files and sensitive logging deliberate concerns.
11. Deliver small, buildable, tested and reversible changes.
12. Do not introduce a dependency, abstraction or technology without a current
    project need and an approved decision when required.
