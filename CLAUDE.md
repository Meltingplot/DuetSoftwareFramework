# CLAUDE.md

## References

- **Duet3D wiki content**: https://github.com/Duet3D/wiki-content — source for the official Duet3D documentation (G-code dictionary, object model, RepRapFirmware and DSF behaviour).

## G-code rules

Do not guess G-code semantics, parameters, or responses. Before writing, changing, or explaining any G-code handling:

1. Check the DSF source (`src/`) for how the command is actually parsed and handled.
2. Check the Duet3D wiki content (link above) for the documented command, its parameters, and expected output.

If the code and the docs disagree, or neither covers the case, say so explicitly instead of filling the gap with an assumption.

## Git workflow

Every feature goes through a feature branch and a pull request. Never commit feature work directly to `v3.6-dev`, `v3.7-dev`, or any other integration branch.

1. Before starting a feature, create a branch from the target integration branch (for example `feature/<short-name>`) and commit there.
2. When the work is complete and tests pass, push the branch and open a PR against the integration branch with `gh pr create`.
3. The feature lands on the integration branch only by merging the PR, never by a direct commit or push.

If a commit was made on an integration branch by mistake and has not been pushed, move it to a feature branch first (`git branch feature/<name>` then `git reset --hard <previous commit>` on the integration branch).

## Versioning rules

Fork versions have the form `<Duet3D version>+mp.N`, for example `3.7.0-rc.2+mp.7`. This covers the DSF version in `src/Directory.Build.props` and the DWC tag pinned in `pkg/dwc-version`.

- Never change the Duet3D version part (`3.7.0-rc.2`). It changes only when an upstream merge brings in a new Duet3D version; the next fork release of that version is `+mp.1`.
- A new release, including a request for a "new rc", only increments `+mp.N`, counting on from the last fork tag of the current Duet3D version (`git tag -l 'v<version>+mp.*'`).
- DSF and DWC count `+mp.N` independently, so DSF `3.7.0-rc.2+mp.7` can ship DWC `3.7.0-rc.2+mp.6`.
