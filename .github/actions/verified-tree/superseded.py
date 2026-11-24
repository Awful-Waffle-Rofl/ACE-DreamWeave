#!/usr/bin/env python3
"""verified-tree superseded-push support: lets a master push run whose commit is already
superseded by a NEWER master tip reuse the verification of that tip.

Why: a merge train lands N squash commits on master in quick succession, and every one of them
starts its own push runs. The train PR already verified the FINAL tree, and the train's -Land
asserts origin/master equals the train branch afterwards. An intermediate member commit A that is
an ancestor of a tip B whose inputs a valid run already verified does not need its own heavy run:
A is never the state master rests on, and B (which contains A) is.

Three sub-commands, all fail CLOSED (any doubt means "run the full job") and all exit 0 on every
lookup problem - failing closed means "run everything", never "fail CI":

  detect   Fetches current origin/master into a private ref and decides whether it is a STRICT
           descendant of this run's commit. Outputs superseded=true|false, tip-sha, reason.
           Only a 0 exit from `git merge-base --is-ancestor` with tip != own counts; a fetch error,
           an unparsable sha, a tip that is not a descendant (force-push or history rewrite) or the
           tip being this very commit all give superseded=false. In a shallow clone the fetch is
           bounded with --depth, so a commit deeper than that under the tip simply reads as "not a
           descendant", which is again the full run.

  pending  (Design step 11, owner ruling 2026-11-18.) Asks the REST API for this workflow's runs on
           the superseding tip sha and outputs pending=true only when there is EXACTLY ONE such run,
           it is a first-attempt (run_attempt 1) push run of this same workflow file on this
           repository's master branch, not a fork, not this very run, and its status is exactly
           'queued' or 'in_progress'. The workflow file is the caller's (VT_WORKFLOW_PATH): each
           workflow asks about ITS OWN run on the tip. For build-test.yml a push run is always the
           full suite (only a pull_request run can be narrow). For content-apply-ci.yml a push run
           always applies the whole manifest twice (or reuses a marker for the tip's exact apply
           inputs), but decides its fidelity step from its own push range, so content-apply-ci.yml
           runs this check only for a push that needs no fidelity. Every other answer is pending=false: no run yet (the tip's push landed
           seconds ago and is not registered), a completed run of any conclusion (a failed or
           cancelled tip never covers, and a completed successful tip covers only through its
           verified-tree marker, the 'verified' basis), a re-run, two or more runs, any API error.
           One call, no retry, no wait. Outputs pending, run-url, run-id, tip-sha, status, reason.

  combine  Merges the own-key lookup, the tip-key lookup and the pending check into the single
           verdict the heavy steps are gated on. verified=true only when this is a master push AND
           either the own lookup verified, or (superseded=true, a valid tip sha, GITHUB_RUN_ATTEMPT
           exactly 1 - a manual re-run always tests for real - and one of two bases):
             basis=verified: the tip lookup verified, and its matched name is one of the tip key
                             names this run computed (the pre-step-11 rule);
             basis=pending:  the tip lookup did NOT verify, and the pending check reported a
                             queued/in-progress full push run for exactly this tip sha.
           Outputs verified, run-url, matched-name, source (own|superseded|none), basis
           (verified|pending, superseded hits only), covering-sha. On a superseded hit it also
           writes a step summary naming the covering tip and the run reused or relied on.

Covered while pending: what if the tip's run later fails or is cancelled? This commit's covered
conclusion is FINAL; nothing comes back to re-run it. That is the accepted trade (the owner ruling):
  * a covered sha is never deployable - the deploy gates refuse it whichever basis covered it,
    because both bases run the same "Covered by newer master tip <sha>" step;
  * the tip's red run turns master red, and the master-red policy applies (the merging session fixes
    forward or reverts);
  * build-test only: when TEST_IMPACT_MODE is shadow or narrow, the test-impact master audit on the red tip
    (audit.py master) walks first-parent back through
    every covered commit into the tip's range, so a covered commit's changes are inside the audited
    range rather than silently assumed green; in mode off, or when the tip run is cancelled or never
    starts, no audit runs and only the deploy gates (no success on the tip, covered step on this sha)
    stand between it and a deploy. content-apply-ci has NO audit at all: for a commit it covers,
    the deploy gates (deploy-prod.yml, deploy-content-prod.yml) are the only thing that refuses it;
  * to make a covered sha deployable or to test it for real, re-run the covered workflow
    (build-test or content-apply-ci) on it: a re-run (attempt 2+) never takes coverage.
The pending tip's own run may itself end up covered, by a still newer tip that landed after this
run looked. Coverage still terminates: each covering tip strictly descends from the commit it
covers, and the newest tip's run is never superseded, so it runs the full suite (or reuses its own
verified tree). Every commit in the chain is in that run's tree, and for build-test also in its
audit range (content-apply-ci has no audit; its deploy gates refuse every covered sha).

Used by build-test.yml and content-apply-ci.yml. A covered run must never record a marker for its
own key (no transitive vouching): build-test records on pull_request only, and content-apply-ci's
record steps require verified != 'true'. A covered run also runs a step named "Covered by newer
master tip <tip sha>", which the deploy gates read through the jobs API to REFUSE deploying that
sha (build-test: deploy-stage.yml, deploy-prod.yml, seed-stage-roster.yml, seed-trial-archetypes.yml;
content-apply: deploy-prod.yml, deploy-content-prod.yml): coverage saves CI minutes, it never
makes an untested sha deployable.

Environment (detect): GITHUB_SHA, optional VT_REMOTE (default origin), VT_BRANCH (default master),
VT_SHALLOW_DEPTH (default 50), VT_FETCH_TIMEOUT_S (default 60).
Environment (pending): GITHUB_TOKEN, GITHUB_REPOSITORY, GITHUB_API_URL, GITHUB_RUN_ID, VT_TIP_SHA,
VT_WORKFLOW_PATH.
Environment (combine): GITHUB_EVENT_NAME, GITHUB_REF, GITHUB_RUN_ATTEMPT, VT_OWN_VERIFIED, VT_OWN_RUN_URL,
VT_OWN_MATCHED_NAME, VT_SUPERSEDED, VT_TIP_SHA, VT_TIP_NAMES (newline-separated), VT_TIP_VERIFIED,
VT_TIP_RUN_URL, VT_TIP_MATCHED_NAME, VT_TIP_PENDING, VT_TIP_PENDING_SHA, VT_TIP_PENDING_RUN_URL,
optional VT_JOB_LABEL (used in the summary heading).
All append to GITHUB_OUTPUT when set (verified/superseded/pending written LAST, in one call),
otherwise print the lines to stdout.
"""

import os
import re
import subprocess
import sys

SHA_RE = re.compile(r"^[0-9a-f]{40}$")
TIP_REF = "refs/verified-tree/master-tip"


def run_git(args, timeout):
    """Run git; return (returncode, stdout stripped). Never raises on a git failure."""
    try:
        p = subprocess.run(["git"] + list(args), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=timeout, check=False)
    except (OSError, subprocess.SubprocessError) as e:
        return 999, "git could not run: %r" % (e,)
    out = p.stdout.decode("utf-8", "replace").strip()
    if p.returncode != 0:
        err = p.stderr.decode("utf-8", "replace").strip()
        if err:
            out = (out + "\n" + err).strip()
    return p.returncode, out


def detect(own_sha, git, remote="origin", branch="master", shallow_depth=50, fetch_timeout=60):
    """Return {'superseded': 'true'|'false', 'tip-sha': str, 'reason': str}."""
    result = {"superseded": "false", "tip-sha": "", "reason": ""}
    own = (own_sha or "").strip().lower()
    if not SHA_RE.match(own):
        result["reason"] = "this run's sha %r is not a 40-hex commit id" % own_sha
        return result
    if not re.match(r"^[A-Za-z0-9._/-]+$", branch) or not re.match(r"^[A-Za-z0-9._-]+$", remote):
        result["reason"] = "unsafe remote/branch name"
        return result

    rc, out = git(["rev-parse", "--is-shallow-repository"], 15)
    if rc != 0 or out not in ("true", "false"):
        result["reason"] = "could not tell whether the clone is shallow (rc=%s: %s)" % (rc, out)
        return result
    # --refmap= (empty): without it git also "opportunistically" moves refs/remotes/origin/master
    # to the fetched tip. Only the private ref below may change.
    fetch = ["fetch", "--no-tags", "--quiet", "--no-recurse-submodules", "--refmap="]
    if out == "true":
        # Bounded: without --depth a shallow clone whose commit is NOT under the tip would pull
        # the tip's whole history. A commit deeper than this reads as "not a descendant".
        fetch.append("--depth=%d" % int(shallow_depth))
    fetch += [remote, "+refs/heads/%s:%s" % (branch, TIP_REF)]
    rc, out = git(fetch, fetch_timeout)
    if rc != 0:
        result["reason"] = "fetch of %s/%s failed (rc=%s): %s" % (remote, branch, rc, out)
        return result

    rc, tip = git(["rev-parse", "--verify", "--quiet", TIP_REF + "^{commit}"], 15)
    tip = tip.strip().lower()
    if rc != 0 or not SHA_RE.match(tip):
        result["reason"] = "could not resolve the fetched %s tip (rc=%s: %s)" % (branch, rc, tip)
        return result
    result["tip-sha"] = tip
    if tip == own:
        result["tip-sha"] = ""
        result["reason"] = "this commit is the current %s tip (not superseded)" % branch
        return result

    rc, out = git(["merge-base", "--is-ancestor", own, tip], 30)
    if rc == 0:
        result["superseded"] = "true"
        result["reason"] = "%s tip %s is a strict descendant of this commit" % (branch, tip)
    elif rc == 1:
        result["tip-sha"] = ""
        result["reason"] = ("%s tip %s is NOT a descendant of this commit (force-push, rewrite, or "
                            "deeper than the shallow fetch)" % (branch, tip))
    else:
        result["tip-sha"] = ""
        result["reason"] = "ancestry check failed (rc=%s): %s" % (rc, out)
    return result


PENDING_STATUSES = ("queued", "in_progress")


def pending(env, api_get):
    """Return {'pending': 'true'|'false', 'run-url', 'run-id', 'tip-sha', 'status', 'reason'}.

    api_get(path) returns parsed JSON or raises; any exception is a fail-closed pending=false."""
    res = {"run-url": "", "run-id": "", "tip-sha": "", "status": "", "reason": "", "pending": "false"}
    tip = (env.get("VT_TIP_SHA") or "").strip().lower()
    repo = (env.get("GITHUB_REPOSITORY") or "").strip()
    workflow_path = (env.get("VT_WORKFLOW_PATH") or "").strip()
    own_run_id = str(env.get("GITHUB_RUN_ID") or "")
    if not SHA_RE.match(tip):
        res["reason"] = "tip sha %r is not a 40-hex commit id" % tip
        return res
    if not re.match(r"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", repo):
        res["reason"] = "GITHUB_REPOSITORY %r is missing or malformed" % repo
        return res
    wf_file = workflow_path.rsplit("/", 1)[-1]
    if not workflow_path.startswith(".github/workflows/") or not re.match(r"^[A-Za-z0-9._-]+\.ya?ml$", wf_file):
        res["reason"] = "VT_WORKFLOW_PATH %r is not a .github/workflows/*.yml path" % workflow_path
        return res
    res["tip-sha"] = tip
    path = ("/repos/%s/actions/workflows/%s/runs?head_sha=%s&event=push&branch=master&per_page=100"
            % (repo, wf_file, tip))
    listing = api_get(path)
    if not isinstance(listing, dict) or not isinstance(listing.get("workflow_runs"), list):
        res["reason"] = "malformed workflow-runs listing for %s" % tip
        return res
    runs = listing["workflow_runs"]
    total = listing.get("total_count")
    if not isinstance(total, int) or total != len(runs):
        res["reason"] = "listing incomplete or inconsistent (total_count=%r, %d rows)" % (total, len(runs))
        return res
    if not runs:
        res["reason"] = ("no %s push run is registered for tip %s yet (it may land in seconds; this run does "
                         "not wait)" % (wf_file, tip))
        return res
    if len(runs) != 1:
        res["reason"] = "%d %s push runs for tip %s, expected exactly one" % (len(runs), wf_file, tip)
        return res
    run = runs[0]
    reasons = []
    if (run.get("head_sha") or "").lower() != tip:
        reasons.append("head_sha=%s (expected %s)" % (run.get("head_sha"), tip))
    if str(run.get("id")) == own_run_id:
        reasons.append("it is this very run")
    if run.get("event") != "push":
        reasons.append("event=%s (expected push: only a push run is always the full suite)" % run.get("event"))
    if run.get("head_branch") != "master":
        reasons.append("branch=%s (expected master)" % run.get("head_branch"))
    if run.get("path") != workflow_path:
        reasons.append("workflow path=%s (expected %s)" % (run.get("path"), workflow_path))
    if run.get("run_attempt") != 1:
        reasons.append("run_attempt=%r (a re-run means an earlier attempt already finished)" % run.get("run_attempt"))
    run_repo = ((run.get("repository") or {}).get("full_name") or "").lower()
    head_repo = ((run.get("head_repository") or {}).get("full_name") or "").lower()
    if run_repo != repo.lower() or head_repo != repo.lower():
        reasons.append("repository=%s head repository=%s (expected %s)" % (run_repo or "<none>", head_repo or "<none>", repo))
    status = run.get("status")
    res["status"] = str(status or "")
    if status not in PENDING_STATUSES:
        reasons.append("status=%s conclusion=%s (only queued or in_progress counts as pending; a completed tip "
                       "covers only through its verified-tree marker)" % (status, run.get("conclusion")))
    url = run.get("html_url") or ""
    if not url:
        reasons.append("no html_url")
    if reasons:
        res["reason"] = "tip %s run %s does not count: %s" % (tip, url or run.get("id"), "; ".join(reasons))
        return res
    res.update({"pending": "true", "run-url": url, "run-id": str(run.get("id")),
                "reason": "tip %s has a %s first-attempt %s push run on master: %s" % (tip, status, wf_file, url)})
    return res


def combine(env):
    """Return the merged verdict dict. Pure function of the env mapping."""
    res = {"run-url": "", "matched-name": "", "source": "none", "basis": "", "covering-sha": "", "reason": "",
           "verified": "false"}
    if env.get("GITHUB_EVENT_NAME") != "push" or env.get("GITHUB_REF") != "refs/heads/master":
        res["reason"] = "not a master push run"
        return res
    if env.get("VT_OWN_VERIFIED") == "true":
        res.update({"verified": "true", "source": "own", "run-url": env.get("VT_OWN_RUN_URL", ""),
                    "matched-name": env.get("VT_OWN_MATCHED_NAME", ""),
                    "reason": "this commit's own key was verified"})
        return res
    if env.get("VT_SUPERSEDED") != "true":
        res["reason"] = "own key not verified and this commit is not superseded"
        return res
    # A manual re-run is a request to test this commit for real, so only a first attempt may be
    # covered by a newer tip. Anything but exactly "1" (absent, malformed, 2+) refuses.
    attempt = (env.get("GITHUB_RUN_ATTEMPT") or "").strip()
    if attempt != "1":
        res["reason"] = "run attempt %r is not 1 (a re-run never takes superseded coverage)" % attempt
        return res
    tip = (env.get("VT_TIP_SHA") or "").strip().lower()
    if not SHA_RE.match(tip):
        res["reason"] = "superseded but the tip sha %r is not a 40-hex commit id" % tip
        return res
    if env.get("VT_TIP_VERIFIED") != "true":
        # Design step 11: covered while the tip's own full push run is queued or in progress.
        if env.get("VT_TIP_PENDING") != "true":
            res["reason"] = ("superseded by %s, but the tip's key is not verified (yet) and the tip has no "
                             "queued or in-progress full push run" % tip)
            return res
        pending_sha = (env.get("VT_TIP_PENDING_SHA") or "").strip().lower()
        if pending_sha != tip:
            res["reason"] = "pending check was for %r, not for the tip %s" % (pending_sha, tip)
            return res
        pending_url = env.get("VT_TIP_PENDING_RUN_URL") or ""
        if not pending_url:
            res["reason"] = "pending check reported no run url for tip %s" % tip
            return res
        res.update({"verified": "true", "source": "superseded", "basis": "pending", "run-url": pending_url,
                    "covering-sha": tip,
                    "reason": "superseded by master tip %s, whose full push run %s is queued or in progress"
                              % (tip, pending_url)})
        return res
    expected = [n.strip() for n in (env.get("VT_TIP_NAMES") or "").splitlines() if n.strip()]
    matched = (env.get("VT_TIP_MATCHED_NAME") or "").strip()
    if not matched or matched not in expected:
        res["reason"] = "tip lookup matched %r, which is not one of the tip key names %r" % (matched, expected)
        return res
    run_url = env.get("VT_TIP_RUN_URL") or ""
    if not run_url:
        res["reason"] = "tip lookup verified but returned no producing run url"
        return res
    res.update({"verified": "true", "source": "superseded", "basis": "verified", "run-url": run_url, "matched-name": matched,
                "covering-sha": tip,
                "reason": "superseded by master tip %s, whose key %s was verified by %s" % (tip, matched, run_url)})
    return res


def one_line(value):
    """Collapse every whitespace run (newlines from git stderr included) to one space, so each
    output is exactly one `key=value` line in GITHUB_OUTPUT."""
    return " ".join(str(value).split())


def write_outputs(result, last_key, log):
    lines = "".join("%s=%s\n" % (k, one_line(v)) for k, v in result.items() if k != last_key)
    lines += "%s=%s\n" % (last_key, one_line(result[last_key]))
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        try:
            with open(out, "a", encoding="utf-8") as f:
                f.write(lines)
            return True
        except Exception as e:
            log("::warning::verified-tree could not write GITHUB_OUTPUT (%r); failing closed (full run)." % (e,))
            return False
    sys.stdout.write(lines)
    return True


def main(argv):
    def log(msg):
        print(msg, flush=True)

    cmd = argv[1] if len(argv) > 1 else ""
    if cmd == "detect":
        try:
            res = detect(os.environ.get("GITHUB_SHA", ""), run_git,
                         remote=os.environ.get("VT_REMOTE") or "origin",
                         branch=os.environ.get("VT_BRANCH") or "master",
                         shallow_depth=int(os.environ.get("VT_SHALLOW_DEPTH") or 50),
                         fetch_timeout=int(os.environ.get("VT_FETCH_TIMEOUT_S") or 60))
        except Exception as e:  # anything unexpected fails closed
            res = {"superseded": "false", "tip-sha": "", "reason": "unexpected error: %r" % (e,)}
        log("verified-tree superseded detect: superseded=%s tip=%s (%s)"
            % (res["superseded"], res["tip-sha"] or "-", res["reason"]))
        write_outputs(res, "superseded", log)
        return 0
    if cmd == "pending":
        try:
            import lookup  # same directory; its api_get is the one REST helper this action uses
            token = os.environ.get("GITHUB_TOKEN") or ""
            api = os.environ.get("GITHUB_API_URL") or "https://api.github.com"
            if not token:
                raise lookup.LookupError_("GITHUB_TOKEN is empty")
            res = pending(os.environ, lambda path: lookup.api_get(api, token, path))
        except Exception as e:  # an API error or anything unexpected fails closed
            res = {"run-url": "", "run-id": "", "tip-sha": "", "status": "",
                   "reason": "pending check failed closed: %s" % (e,), "pending": "false"}
        log("verified-tree pending check: pending=%s (%s)" % (res["pending"], res["reason"]))
        write_outputs(res, "pending", log)
        return 0
    if cmd == "combine":
        try:
            res = combine(os.environ)
        except Exception as e:
            res = {"run-url": "", "matched-name": "", "source": "none", "basis": "", "covering-sha": "",
                   "reason": "unexpected error: %r" % (e,), "verified": "false"}
        written = write_outputs(res, "verified", log)
        if written and res["verified"] == "true":
            log("VERIFIED (%s): %s - heavy steps will be skipped." % (res["source"], res["reason"]))
            if res["source"] == "superseded":
                summary = os.environ.get("GITHUB_STEP_SUMMARY")
                label = os.environ.get("VT_JOB_LABEL") or "this job"
                if summary:
                    try:
                        with open(summary, "a", encoding="utf-8") as f:
                            f.write("### %s covered by a newer master tip\n\n" % label)
                            if res["basis"] == "pending":
                                # Only build-test has a master audit (audit.py master); never claim one
                                # for any other job.
                                if label == "build-test":
                                    after = ("this commit is inside its audit range.")
                                else:
                                    after = ("no audit covers this workflow; the deploy gates refuse this sha.")
                                f.write("This commit (`%s`) is superseded by master tip `%s`, a strict "
                                        "descendant whose own full push run %s was queued or in progress "
                                        "when this run looked (covered while pending, design step 11). That "
                                        "run had NOT passed yet: if it fails, master is red at the tip and "
                                        "%s This commit's own inputs were "
                                        "NOT separately verified; heavy steps were skipped, no marker was "
                                        "recorded, and this sha is not deployable.\n\n"
                                        % (os.environ.get("GITHUB_SHA", "?"), res["covering-sha"], res["run-url"], after))
                            else:
                                f.write("This commit (`%s`) is superseded by master tip `%s`, a strict "
                                        "descendant whose inputs were already verified by %s (marker `%s`). "
                                        "This commit's own inputs were NOT separately verified; heavy steps "
                                        "were skipped and no marker was recorded for this commit's own key.\n\n"
                                        % (os.environ.get("GITHUB_SHA", "?"), res["covering-sha"],
                                           res["run-url"], res["matched-name"]))
                    except Exception as e:
                        log("::warning::could not write the step summary: %r" % (e,))
                if res["basis"] == "pending":
                    log("::notice::covered by master tip %s, whose full push run %s is still pending; this commit's own inputs were not separately verified."
                        % (res["covering-sha"], res["run-url"]))
                else:
                    log("::notice::covered by master tip %s (verified by %s); this commit's own inputs were not separately verified."
                        % (res["covering-sha"], res["run-url"]))
        else:
            log("NOT VERIFIED: %s - running the full job." % res["reason"])
        return 0
    log("::error::verified-tree superseded.py: unknown sub-command %r (expected detect, pending or combine)" % cmd)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
