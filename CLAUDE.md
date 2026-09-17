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
