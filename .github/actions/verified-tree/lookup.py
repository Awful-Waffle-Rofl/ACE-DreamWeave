#!/usr/bin/env python3
"""verified-tree lookup: decide whether a master push run may reuse a PR run's, or an earlier
master push run's, verification.

Given one or more candidate artifact names (newline-separated, in preference order), asks the
REST API for every artifact in this repo with exactly that name, and accepts a name only when
ALL of the following hold:

  * at least one unexpired artifact carries the name;
  * EVERY unexpired artifact carrying the name was produced by a run that is completed, concluded
    'success', ran the SAME workflow file as this run, ran on this repository with a head
    repository that is this repository (not a fork), and was EITHER a 'pull_request' event OR a
    'push' event on this repository's 'master' branch (the only branch this workflow's push
    trigger fires on, but checked explicitly rather than trusted);
  * the listing was complete (total_count fits in one page).

Anything else - an HTTP or network error, a missing token, a malformed response, zero matches,
or any candidate that fails a check (the "ambiguous" case) - rejects that name. If no name is
accepted the result is verified=false and the caller runs its heavy steps in full. This script
never exits non-zero on a lookup problem: failing closed means "run everything", not "fail CI".

Environment:
  GITHUB_TOKEN        token with actions:read (required)
  GITHUB_REPOSITORY   owner/name (required)
  GITHUB_API_URL      default https://api.github.com
  GITHUB_RUN_ID       this run's id; a candidate produced by this very run is rejected
  GITHUB_OUTPUT       if set, outputs are appended here (verified, run-url, run-id, matched-name)
  VT_NAMES            newline-separated candidate artifact names, preference order (required)
  VT_WORKFLOW_PATH    e.g. .github/workflows/build-test.yml (required)
"""

import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

PER_PAGE = 100
TIMEOUT_S = 20


class LookupError_(Exception):
    pass


def api_get(api, token, path):
    req = urllib.request.Request(
        api.rstrip("/") + path,
        headers={
            "Authorization": "Bearer " + token,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "verified-tree-lookup",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_S) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise LookupError_("HTTP %d on GET %s" % (e.code, path))
    except (urllib.error.URLError, OSError, ValueError) as e:
        raise LookupError_("request failed on GET %s: %s" % (path, e))


def validate_run(run, repo, workflow_path, own_run_id):
    """Return a list of reasons this run cannot vouch for the tree (empty list = acceptable)."""
    reasons = []
    if str(run.get("id")) == str(own_run_id):
        reasons.append("produced by this very run")
    if run.get("status") != "completed":
        reasons.append("status=%s (not completed)" % run.get("status"))
    if run.get("conclusion") != "success":
        reasons.append("conclusion=%s (not success)" % run.get("conclusion"))
    event = run.get("event")
    if event not in ("pull_request", "push"):
        reasons.append("event=%s (not pull_request or push)" % event)
    elif event == "push" and run.get("head_branch") != "master":
        reasons.append("push run on branch=%s (expected master)" % run.get("head_branch"))
    if run.get("path") != workflow_path:
        reasons.append("workflow path=%s (expected %s)" % (run.get("path"), workflow_path))
    run_repo = ((run.get("repository") or {}).get("full_name") or "").lower()
    head_repo = ((run.get("head_repository") or {}).get("full_name") or "").lower()
    if run_repo != repo.lower():
        reasons.append("repository=%s (expected %s)" % (run_repo or "<none>", repo))
    if head_repo != repo.lower():
        reasons.append("head repository=%s (fork or unknown; expected %s)" % (head_repo or "<none>", repo))
    return reasons


def check_name(api, token, repo, name, workflow_path, own_run_id, log):
    """Return (accepted_run or None). Raises LookupError_ on any API problem."""
    q = urllib.parse.urlencode({"name": name, "per_page": PER_PAGE})
    listing = api_get(api, token, "/repos/%s/actions/artifacts?%s" % (repo, q))
    if not isinstance(listing, dict) or "artifacts" not in listing:
        raise LookupError_("malformed artifact listing for %s" % name)
    total = listing.get("total_count", 0)
    arts = [a for a in listing["artifacts"] if a.get("name") == name]
    if total > PER_PAGE or len(listing["artifacts"]) != min(total, PER_PAGE):
        log("  %s: listing incomplete (total_count=%s) - rejecting as ambiguous" % (name, total))
        return None
    live = [a for a in arts if not a.get("expired")]
    if not live:
        log("  %s: no unexpired artifact with this exact name" % name)
        return None
    accepted = None
    for a in live:
        run_id = (a.get("workflow_run") or {}).get("id")
        if not run_id:
            log("  %s: artifact %s has no producing run id - rejecting as ambiguous" % (name, a.get("id")))
            return None
        run = api_get(api, token, "/repos/%s/actions/runs/%s" % (repo, run_id))
        reasons = validate_run(run, repo, workflow_path, own_run_id)
        if reasons:
            log("  %s: producer run %s rejected: %s - rejecting the name (ambiguous/invalid producer)"
                % (name, run.get("html_url") or run_id, "; ".join(reasons)))
            return None
        log("  %s: producer run %s OK (pull_request, success, %s)" % (name, run.get("html_url"), workflow_path))
        if accepted is None:
            accepted = run
    return accepted


def main():
    def log(msg):
        print(msg, flush=True)

    result = {"verified": "false", "run-url": "", "run-id": "", "matched-name": ""}
    try:
        token = os.environ.get("GITHUB_TOKEN") or ""
        repo = os.environ.get("GITHUB_REPOSITORY") or ""
        api = os.environ.get("GITHUB_API_URL") or "https://api.github.com"
        own_run_id = os.environ.get("GITHUB_RUN_ID") or ""
        workflow_path = (os.environ.get("VT_WORKFLOW_PATH") or "").strip()
        names = [n.strip() for n in (os.environ.get("VT_NAMES") or "").splitlines() if n.strip()]
        if not token:
            raise LookupError_("GITHUB_TOKEN is empty")
        if not repo or "/" not in repo:
            raise LookupError_("GITHUB_REPOSITORY is missing or malformed: %r" % repo)
        if not workflow_path:
            raise LookupError_("VT_WORKFLOW_PATH is empty")
        if not names:
            raise LookupError_("no candidate names given")
        log("verified-tree lookup in %s for %s" % (repo, workflow_path))
        for name in names:
            run = check_name(api, token, repo, name, workflow_path, own_run_id, log)
            if run is not None:
                result.update({
                    "verified": "true",
                    "run-url": run.get("html_url") or "",
                    "run-id": str(run.get("id") or ""),
                    "matched-name": name,
                })
                break
    except LookupError_ as e:
        log("::warning::verified-tree lookup failed closed (full run): %s" % e)
    except Exception as e:  # anything unexpected also fails closed
        log("::warning::verified-tree lookup failed closed on an unexpected error (full run): %r" % e)

    # The output write is inside its own try: an I/O failure here must not exit non-zero (that
    # would fail the whole push job). The block is written in ONE call with verified LAST, so a
    # partial write can never leave verified=true behind without the rest; an absent or empty
    # verified output reads as != 'true' in the caller, which is the full run.
    lines = "".join("%s=%s\n" % (k, v) for k, v in result.items() if k != "verified")
    lines += "verified=%s\n" % result["verified"]
    out = os.environ.get("GITHUB_OUTPUT")
    written = True
    if out:
        try:
            with open(out, "a", encoding="utf-8") as f:
                f.write(lines)
        except Exception as e:
            written = False
            log("::warning::verified-tree could not write GITHUB_OUTPUT (%r); failing closed (full run)." % e)
            log("verified=false")

    # The verdict line is printed only after the write, so the log never claims a skip that the
    # step outputs do not carry.
    if written and result["verified"] == "true":
        log("VERIFIED: this tree was already verified by %s (artifact %s) - heavy steps will be skipped."
            % (result["run-url"], result["matched-name"]))
    else:
        log("NOT VERIFIED: running the full job.")
    if not out:
        sys.stdout.write(lines)
    return 0


if __name__ == "__main__":
    sys.exit(main())
