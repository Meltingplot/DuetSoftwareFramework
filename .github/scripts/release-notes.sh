#!/bin/bash
# Writes the release notes of a version tag (e.g. v3.7.0-rc.1+mp.8) to stdout: the message of the tag if
# it is annotated, followed by the changelog of every release of the same base version reachable from
# the tag (v3.7.0-rc.1+mp.8, +mp.7, ... +mp.1), newest first, back to the previous release (the base
# version tag v3.7.0-rc.1 if it exists, otherwise the version tag before the oldest of these releases).
#
# The entries of a release are the commits on the first-parent history between it and the release
# before it: a merged pull request is listed by its title and number, any other commit by its subject.
# Version bumps and the automated API documentation updates are left out.
#
# Usage: release-notes.sh <tag>   (GITHUB_REPOSITORY adds a link to the full diff)
set -euo pipefail

tag="$1"
version="${tag#v}"
base="v${version%%+*}"

# Header: annotated tag message, or just the version for a lightweight tag
if [ "$(git cat-file -t "$tag")" = "tag" ]; then
    printf '%s\n' "$(git tag -l --format='%(contents)' "$tag")"
else
    echo "DSF $version"
fi

# Releases of the same base version that are part of this tag's history, newest first
releases=()
while read -r release; do
    if [ -n "$release" ] && git merge-base --is-ancestor "$release" "$tag"; then
        releases+=("$release")
    fi
done < <(git tag -l "$base+mp.*" --sort=-v:refname)
if [ ${#releases[@]} -eq 0 ] || [ "${releases[0]}" != "$tag" ]; then
    releases=("$tag" "${releases[@]}")
fi

# The previous release the changelog goes back to
oldest="${releases[-1]}"
if [ "$base" != "$oldest" ] && git rev-parse -q --verify "refs/tags/$base" >/dev/null &&
   git merge-base --is-ancestor "$base" "$oldest"; then
    previous="$base"
else
    previous="$(git describe --tags --abbrev=0 --match 'v[0-9]*' --exclude "$base+mp.*" "$oldest^" 2>/dev/null || true)"
fi

# Prints the changelog entries of a revision range
entries() {
    local commit subject title count=0
    for commit in $(git rev-list --first-parent "$1"); do
        subject="$(git log -1 --format=%s "$commit")"
        if [[ "$subject" =~ ^Merge\ pull\ request\ \#([0-9]+)\  ]]; then
            title="$(git log -1 --format=%b "$commit" | head -n1)"
            title="${title:-$subject}"
            entry="$title (#${BASH_REMATCH[1]})"
        else
            title="$subject"
            entry="$subject ($(git rev-parse --short "$commit"))"
        fi
        if [[ "$title" =~ ^Version\ [0-9] ]] || [ "$title" = "Updated DuetAPI documentation" ]; then
            continue
        fi
        echo "* $entry"
        count=$((count + 1))
    done
    if [ $count -eq 0 ]; then
        echo "* No functional changes"
    fi
}

echo
if [ -n "$previous" ]; then
    echo "## Changes since ${previous#v}"
else
    echo "## Changes"
fi
for i in "${!releases[@]}"; do
    release="${releases[$i]}"
    if [ $((i + 1)) -lt ${#releases[@]} ]; then
        range="${releases[$((i + 1))]}..$release"
    elif [ -n "$previous" ]; then
        range="$previous..$release"
    else
        range="$release"
    fi
    echo
    echo "### ${release#v}"
    entries "$range"
done

if [ -n "$previous" ] && [ -n "${GITHUB_REPOSITORY:-}" ]; then
    echo
    echo "**Full Changelog**: https://github.com/$GITHUB_REPOSITORY/compare/$previous...$tag"
fi
