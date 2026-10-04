# Project Reforged Updater

## Commit messages

Use Conventional Commits. release-please reads them to pick each version and write
the changelog, so a message in another style is skipped from the release notes.

- Format: `type(scope): summary`. The scope is optional, for example `feat(gui): ...`.
- `feat` makes a minor release. `fix` and `perf` make a patch release.
- Add `!` after the type, or a `BREAKING CHANGE:` line in the body, for a major release.
- `docs`, `refactor`, `test`, `build`, `ci`, `chore`, and `style` don't make a release on their own.
- Write the summary in the imperative, in lower case, with no full stop.
- Every commit gets one of these, including docs-only and workflow-only changes.

The `commit-msg` hook in `.githooks/` rejects other styles. Turn it on in a new clone with
`git config core.hooksPath .githooks`. Never skip it with `--no-verify`.

## Releases

Don't tag by hand. Merging the release-please pull request tags the release, and the tag
starts the build and publishes the files. See `docs/development.md`.
