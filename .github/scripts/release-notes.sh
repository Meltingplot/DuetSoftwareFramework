#!/bin/bash
# Writes the release notes of a version tag (e.g. v3.7.0-rc.1+mp.8) to stdout: the message of the tag if
# it is annotated, the versions its packages carry, followed by the changelog of every release of the
# same base version reachable from the tag (v3.7.0-rc.1+mp.8, +mp.7, ... +mp.1), newest first, back to
# the previous release (the base version tag v3.7.0-rc.1 if it exists, otherwise the version tag before
# the oldest of these releases).
#
# The entries of a release are the commits on the first-parent history between it and the release
# before it: a merged pull request is listed by its title and number, any other commit by its subject.
# Version bumps and the automated API documentation updates are left out.
#
# DWC lives in its own repository, so a release that only picks up newer DWC sources has nothing to show
# in this one. Such a release moves the DWC ref pinned in pkg/dwc-version, and its entries then continue
# with the DWC commits that pin change brings in, read from a clone of the DWC fork. DWC_MIRROR reuses an
# existing clone instead of making one; if neither works, the release still names its DWC version and
# links the comparison. For the first release carrying the pin, the value the pin was introduced with
# stands in for the previous release, which has none to read.
#
# Usage: release-notes.sh <tag>   (GITHUB_REPOSITORY adds a link to the full diff)
set -euo pipefail

tag="$1"
version="${tag#v}"
base="v${version%%+*}"
dwc_repo="${DWC_REPO:-https://github.com/Meltingplot/DuetWebControl.git}"
dwc_slug="$(echo "$dwc_repo" | sed -e 's,\.git$,,' -e 's,.*[:/]\([^/]*/[^/]*\)$,\1,')"

# The DWC ref pinned at a revision, empty for a release that predates the pin
dwc_pin() {
    { git show "$1:pkg/dwc-version" 2>/dev/null || true ; } |
        sed -e 's/#.*//' -e 's/[[:space:]]//g' -e '/^$/d'
}

# A clone of the DWC fork, made once and only when a pin change has to be turned into entries. Not
# getting one is not fatal, the notes then fall back to a comparison link
dwc_git=""
dwc_tried=0
dwc_clone() {
    [ $dwc_tried -eq 1 ] && return
    dwc_tried=1
    if [ -n "${DWC_MIRROR:-}" ] && [ -d "$DWC_MIRROR" ]; then
        dwc_git="$(git -C "$DWC_MIRROR" rev-parse --absolute-git-dir 2>/dev/null || true)"
        [ -n "$dwc_git" ] && return
    fi
    # Only commits and trees are needed to list subjects, so the blobs can stay on the server
    dwc_git="$(mktemp -d)/DuetWebControl.git"
    if ! git clone -q --bare --filter=blob:none "$dwc_repo" "$dwc_git"; then
        echo "Could not clone $dwc_repo, listing DWC versions without their changes" >&2
        dwc_git=""
    fi
}

# Prints the changelog entries of a revision range in the repository of $1 (empty for this one)
entries() {
    local range="$2" commit subject title entry
    local -a gitcmd=(git)
    [ -n "$1" ] && gitcmd=(git --git-dir="$1")
    for commit in $("${gitcmd[@]}" rev-list --first-parent "$range"); do
        subject="$("${gitcmd[@]}" log -1 --format=%s "$commit")"
        if [[ "$subject" =~ ^Merge\ pull\ request\ \#([0-9]+)\  ]]; then
            title="$("${gitcmd[@]}" log -1 --format=%b "$commit" | head -n1)"
            title="${title:-$subject}"
            entry="$title (#${BASH_REMATCH[1]})"
        else
            title="$subject"
            entry="$subject ($("${gitcmd[@]}" rev-parse --short "$commit"))"
        fi
        if [[ "$title" =~ ^Version\ [0-9] ]] || [ "$title" = "Updated DuetAPI documentation" ]; then
            continue
        fi
        echo "* $entry"
    done
}

# Prints the DWC part of a release's changelog: a heading naming the DWC version it moves the pin to,
# followed by the DWC commits that brings in. Empty for a release that leaves the pin alone
dwc_entries() {
    local from="$1" to="$2" range old new introduced body
    new="$(dwc_pin "$to")"
    [ -z "$new" ] && return
    range="${from:+$from..}$to"
    old=""
    [ -n "$from" ] && old="$(dwc_pin "$from")"
    if [ -z "$old" ]; then
        # The release before this one predates the pin. The value the pin was introduced with stands
        # in for it, being the DWC version that release was built from
        introduced="$(git rev-list --reverse "$range" -- pkg/dwc-version | head -n1)"
        [ -n "$introduced" ] && old="$(dwc_pin "$introduced")"
    fi
    # Nothing to list for a release that leaves the pin where it was
    if [ -z "$old" ] || [ "$old" = "$new" ]; then
        return
    fi

    echo
    echo "#### DuetWebControl ${new#v}"
    dwc_clone
    if [ -n "$dwc_git" ] &&
       git --git-dir="$dwc_git" rev-parse -q --verify "$old^{commit}" >/dev/null &&
       git --git-dir="$dwc_git" rev-parse -q --verify "$new^{commit}" >/dev/null; then
        body="$(entries "$dwc_git" "$old..$new")"
        if [ -n "$body" ]; then
            printf '%s\n' "$body"
        else
            echo "* No functional changes"
        fi
    else
        echo "* Changes: https://github.com/$dwc_slug/compare/$old...$new"
    fi
}

# Header: annotated tag message, or just the version for a lightweight tag
if [ "$(git cat-file -t "$tag")" = "tag" ]; then
    printf '%s\n' "$(git tag -l --format='%(contents)' "$tag")"
else
    echo "DSF $version"
fi

# The versions of the packages this release is built from. DWC and the virtual SD card are versioned
# apart from DSF, so the tag alone does not say which of them a release carries
dwcver="$(dwc_pin "$tag")"
sdver="$({ git show "$tag:pkg/deb/duetsd/DEBIAN/control" 2>/dev/null || true ; } |
    sed -n -E 's/^Version:[[:space:]]*//p')"
components="**DSF** $version"
[ -n "$dwcver" ] && components="$components · **DuetWebControl** ${dwcver#v}"
[ -n "$sdver" ] && components="$components · **Virtual SD card** $sdver"
echo
echo "$components"

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

echo
if [ -n "$previous" ]; then
    echo "## Changes since ${previous#v}"
else
    echo "## Changes"
fi

# The entries below are collected in subshells, which cannot hand a clone back, so make the one they
# all share here. Only a range that touches the pin needs it
if [ -n "$dwcver" ] && [ -n "$(git rev-list -n1 "${previous:+$previous..}$tag" -- pkg/dwc-version)" ]; then
    dwc_clone
fi
for i in "${!releases[@]}"; do
    release="${releases[$i]}"
    if [ $((i + 1)) -lt ${#releases[@]} ]; then
        from="${releases[$((i + 1))]}"
    else
        from="$previous"
    fi
    echo
    echo "### ${release#v}"
    body="$(entries "" "${from:+$from..}$release")"
    dwc_body="$(dwc_entries "$from" "$release")"
    if [ -z "$body" ] && [ -z "$dwc_body" ]; then
        echo "* No functional changes"
    else
        [ -n "$body" ] && printf '%s\n' "$body"
        [ -n "$dwc_body" ] && printf '%s\n' "$dwc_body"
    fi
done

if [ -n "$previous" ] && [ -n "${GITHUB_REPOSITORY:-}" ]; then
    echo
    echo "**Full Changelog**: https://github.com/$GITHUB_REPOSITORY/compare/$previous...$tag"
fi
