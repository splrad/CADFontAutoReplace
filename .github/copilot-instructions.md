When reviewing pull requests in this repository, focus on concrete correctness, safety, workflow, release, and compatibility risks.

For every review comment, start the comment body with exactly one severity line:

- `Severity: blocking` for issues that should block merging until fixed.
- `Severity: suggestion` for non-blocking improvements, style suggestions, or optional cleanup.

Use `Severity: blocking` only for bugs, security issues, broken CI/CD behavior, release/package regressions, data loss risk, or changes that invalidate the branch and approval rules documented in this repository.

Do not mark ordinary refactors, naming preferences, formatting concerns, or subjective readability suggestions as blocking.
