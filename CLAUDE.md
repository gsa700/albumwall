# Working in this repository

This repository is PUBLIC (since 2026-09-28). Everything committed and pushed here
is published: it can be copied, and a force-push does not fully take it back
(GitHub keeps old commits reachable by id). The rules below exist because they
were broken once, the day it went public, and had to be repaired by rewriting
public history.

## Privacy: before every commit and every push

1. **Author email is the GitHub no-reply address**, never a personal one. In every
   clone, on every machine, before the first commit:

       git config user.email "298062495+gsa700@users.noreply.github.com"
       git config user.name  "David Erickson"

   Check with `git log -1 --format='%ae %ce'` after committing.
2. **No private network details in any file or commit message**: no LAN IP
   addresses (10.x, 192.168.x, 172.16-31.x), no share paths built on them. Use
   the machine's NAME instead (Techbench, Hambench, NASBOX, Pi5-POE are fine; he
   chose to keep machine names). `\\NASBOX\NAS_data\...`, not an address.
3. **No personal email, keys, tokens or passwords** anywhere, including test
   notes and docs/windows-notes.md.
4. **Scan before pushing**:

       git log origin/master..HEAD --format='%ae%n%ce' | sort -u      # only the no-reply address
       git diff origin/master..HEAD | grep -nE '\b(10(\.[0-9]{1,3}){3}|192\.168(\.[0-9]{1,3}){2}|172\.(1[6-9]|2[0-9]|3[01])(\.[0-9]{1,3}){2})\b'

   Anything found: fix it in the commit BEFORE pushing. Once pushed, it is public.
5. **Never push old history.** Some clones keep a local `pre-rewrite-*` branch or
   an old pre-public history; those must never be pushed. The private original is
   `gsa700/albumwall-private` and stays separate. If `git push` is rejected as
   non-fast-forward, do not force it: fetch and reset onto `origin/master`, then
   ask.

`scripts/release.sh` refuses to cut a release if any commit author is not the
no-reply address or the tree contains a private address, but that only catches
it at release time; the rules above are for every push.

## Releases

- The app: `scripts/release.sh X.Y.Z` (draft; `--publish` to publish;
  `--dry-run` to build only). Bump `<Version>` in its own commit first.
- The audio engine: `scripts/release-libmpv.sh libmpv-X.Y.Z-N`, then point
  `scripts/LIBMPV_RELEASE` at it. Gapless must pass on all three builds first
  (`tools/gapless-check.cs`, with `GAPLESS_AF` for the player's filter chain),
  on Windows ON WINDOWS.
- Publishing an app release offers it to every installed copy. It is his call,
  every time.

## More

The design record is `docs/roadmap.md` (what is wanted and decided) and the
commit messages (what was learned). Windows specifics: `docs/windows-notes.md`.
