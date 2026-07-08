When reviewing pull requests in this repository, write the pull request overview and every review comment in Simplified Chinese.

Focus on concrete correctness, safety, workflow, release, and compatibility risks.

For every review comment, start the comment body with exactly one machine-readable severity line:

- `严重程度：阻断` for issues that should block merging until fixed.
- `严重程度：建议` for non-blocking improvements, style suggestions, or optional cleanup.

Use `严重程度：阻断` only for bugs, security issues, broken CI/CD behavior, release/package regressions, data loss risk, or changes that invalidate the branch and approval rules documented in this repository.

Do not mark ordinary refactors, naming preferences, formatting concerns, or subjective readability suggestions as blocking.
