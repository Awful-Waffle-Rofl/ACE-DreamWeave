#!/usr/bin/env python3
"""Self-test for the verified-tree superseded-push skip (superseded.py, including design step 11's
covered-while-pending rule and a mutation pass over it), the lookup it relies on
(lookup.py), and the deploy gates' refusal of a covered build-test run (the "Refuse a build-test
covered by a newer master tip" step of deploy-prod.yml, deploy-stage.yml,
seed-stage-roster.yml and seed-trial-archetypes.yml, run for real against recorded API JSON; needs jq and PyYAML, both on the
GitHub ubuntu images). It also covers content-apply-ci.yml's copy of the same coverage: its step wiring
(with a mutation pass), its superseding-tip key computed from a throwaway repository, and the
"Refuse a content-apply covered by a newer master tip" step of deploy-prod.yml and deploy-content-prod.yml. Stdlib only. Builds throwaway git repositories and a local fake GitHub REST API in
a temp directory, so it touches neither this checkout's refs nor the network. Run from anywhere:

    python3 .github/actions/verified-tree/test_superseded.py

Exits non-zero if any case fails. Not wired into CI: it is a developer check for changes to this
action, like tools/test-content-lint.sh.
"""

import http.server
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True  # never leave __pycache__ in the checkout
sys.path.insert(0, HERE)
import superseded  # noqa: E402

FAILS = []
PASSES = [0]


def check(label, cond, detail=""):
    if cond:
        PASSES[0] += 1
        print("PASS  " + label)
    else:
        FAILS.append(label)
        print("FAIL  " + label + ("  -- " + detail if detail else ""))


def git(cwd, *args):
    env = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
               GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid",
               GIT_CONFIG_NOSYSTEM="1", HOME=cwd)
    p = subprocess.run(["git"] + list(args), cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       env=env, check=False)
    if p.returncode != 0:
        raise RuntimeError("git %s failed in %s: %s" % (" ".join(args), cwd, p.stderr.decode()))
    return p.stdout.decode().strip()


def commit(work, name):
    with open(os.path.join(work, name), "w") as f:
        f.write(name + "\n")
    git(work, "add", name)
    git(work, "commit", "-q", "-m", name)
    return git(work, "rev-parse", "HEAD")


def runner_in(cwd):
    def run(args, timeout):
        p = subprocess.run(["git"] + list(args), cwd=cwd, stdout=subprocess.PIPE,
                           stderr=subprocess.PIPE, timeout=timeout, check=False)
        return p.returncode, p.stdout.decode().strip()
    return run


def real_runner_in(cwd):
    """superseded.run_git itself (stderr folded in, as in CI), run from cwd."""
    def run(args, timeout):
        here = os.getcwd()
        os.chdir(cwd)
        try:
            return superseded.run_git(args, timeout)
        finally:
            os.chdir(here)
    return run


def detect_tests(tmp):
    origin = os.path.join(tmp, "origin.git")
    work = os.path.join(tmp, "work")
    git(tmp, "init", "-q", "--bare", "-b", "master", origin)
    git(tmp, "init", "-q", "-b", "master", work)
    git(work, "remote", "add", "origin", "file://" + origin)
    for i in range(5):
        commit(work, "base%d" % i)
    a = commit(work, "A")
    git(work, "push", "-q", "origin", "master")

    # The CI clone of commit A, full history (content-apply-ci / content-lint) and shallow
    # (build-test, fetch-depth 1).
    full = os.path.join(tmp, "full")
    git(tmp, "clone", "-q", "file://" + origin, full)
    shallow = os.path.join(tmp, "shallow")
    git(tmp, "clone", "-q", "--depth=1", "file://" + origin, shallow)

    r = superseded.detect(a, runner_in(full))
    check("detect: not superseded (tip is this commit) -> full run",
          r["superseded"] == "false" and r["tip-sha"] == "", repr(r))

    check("detect: malformed own sha -> full run",
          superseded.detect("not-a-sha", runner_in(full))["superseded"] == "false")

    # Train lands B (the tip) on top of A.
    b = commit(work, "B")
    git(work, "push", "-q", "origin", "master")
    origin_master_before = git(full, "rev-parse", "refs/remotes/origin/master")
    r = superseded.detect(a, runner_in(full))
    check("detect: superseded, full clone -> superseded=true tip=B",
          r["superseded"] == "true" and r["tip-sha"] == b, repr(r))
    check("detect: full clone stays non-shallow after the fetch",
          git(full, "rev-parse", "--is-shallow-repository") == "false")
    check("detect: origin/master is untouched (private ref only)",
          git(full, "rev-parse", "refs/remotes/origin/master") == origin_master_before)
    check("detect: HEAD is untouched", git(full, "rev-parse", "HEAD") == a)

    r = superseded.detect(a, runner_in(shallow))
    check("detect: superseded, shallow clone -> superseded=true tip=B",
          r["superseded"] == "true" and r["tip-sha"] == b, repr(r))
    check("detect: shallow clone can read the tip's tree afterwards",
          len(git(shallow, "rev-parse", b + "^{tree}")) == 40)

    # Bounded depth: a commit deeper under the tip than the shallow fetch reaches reads as "not a
    # descendant" (full run), never as a false "superseded".
    shallow2 = os.path.join(tmp, "shallow2")
    git(tmp, "clone", "-q", "--depth=1", "file://" + origin, shallow2)  # at B
    commit(work, "C1")
    commit(work, "C2")
    c3 = commit(work, "C3")
    git(work, "push", "-q", "origin", "master")
    r = superseded.detect(b, runner_in(shallow2), shallow_depth=2)
    check("detect: shallow, commit deeper than --depth -> full run",
          r["superseded"] == "false", repr(r))
    r = superseded.detect(b, runner_in(shallow2), shallow_depth=10)
    check("detect: shallow, commit within --depth -> superseded=true",
          r["superseded"] == "true" and r["tip-sha"] == c3, repr(r))

    # Force-push / history rewrite: master now points at a commit that does NOT descend from B
    # (B is present in the full clone from the fetch above, as a checked-out commit would be).
    git(work, "reset", "-q", "--hard", a)
    rewritten = commit(work, "rewrite")
    git(work, "push", "-q", "--force", "origin", "master")
    r = superseded.detect(b, runner_in(full))
    check("detect: tip not a descendant (force-push) -> full run",
          r["superseded"] == "false" and r["tip-sha"] == "" and "NOT a descendant" in r["reason"], repr(r))
    r = superseded.detect(a, runner_in(full))
    check("detect: after the rewrite A is still an ancestor of the new tip -> superseded",
          r["superseded"] == "true" and r["tip-sha"] == rewritten, repr(r))

    # Fetch failure (remote gone) -> full run.
    git(full, "remote", "set-url", "origin", "file://" + os.path.join(tmp, "does-not-exist.git"))
    r = superseded.detect(a, runner_in(full), fetch_timeout=20)
    check("detect: fetch error -> full run", r["superseded"] == "false" and "fetch" in r["reason"], repr(r))

    # Not a git repository at all.
    nogit = os.path.join(tmp, "nogit")
    os.mkdir(nogit)
    r = superseded.detect(a, runner_in(nogit))
    check("detect: not a repository -> full run", r["superseded"] == "false", repr(r))

    # Every GITHUB_OUTPUT line must be exactly one key=value line, even when git's stderr is
    # multi-line (a fetch against a missing remote prints several lines).
    r = superseded.detect(a, real_runner_in(full), fetch_timeout=20)
    check("detect: positive control - a failing fetch's raw reason is multi-line",
          "\n" in r["reason"], repr(r["reason"]))
    out = os.path.join(tmp, "detect-fail.out")
    p = subprocess.run([sys.executable, os.path.join(HERE, "superseded.py"), "detect"], cwd=full,
                       env=dict(os.environ, GITHUB_SHA=a, GITHUB_OUTPUT=out),
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    raw = open(out).read()
    lines = raw.split("\n")
    check("detect CLI: failing fetch -> exit 0, every output line is one key=value line, verdict last",
          p.returncode == 0 and raw.endswith("\n") and lines[-1] == ""
          and all(re.match(r"^[a-z-]+=[^\n]*$", ln) for ln in lines[:-1])
          and lines[-2] == "superseded=false" and len(lines[:-1]) == 3, repr(raw))

    # The detect sub-command end to end: always exits 0, verdict line written last.
    out = os.path.join(tmp, "detect.out")
    p = subprocess.run([sys.executable, os.path.join(HERE, "superseded.py"), "detect"], cwd=shallow,
                       env=dict(os.environ, GITHUB_SHA=a, GITHUB_OUTPUT=out),
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    lines = open(out).read().splitlines()
    check("detect CLI: exit 0, superseded=true written last",
          p.returncode == 0 and lines[-1] == "superseded=true", repr((p.returncode, lines)))


# ---- deploy gates: refuse a build-test run covered by a newer tip -------------------------------
#
# Runs the REAL "Refuse a build-test covered by a newer master tip" step of every gate workflow
# (extracted from the YAML) with a fake `gh` on PATH that serves recorded API JSON. The fixtures
# are trimmed from real responses (GET commits/<sha>/check-runs and GET actions/jobs/<id> for
# master 6bb912853, recorded 2026-11-17) with repository identifiers replaced; the covered job is
# that recording plus the build-test step a covered run adds.

GATE_STEP = "Refuse a build-test covered by a newer master tip"
GATE_WORKFLOWS = ["deploy-prod.yml", "deploy-stage.yml", "seed-stage-roster.yml", "seed-trial-archetypes.yml"]
GATE_SHA = "6bb912853280afd33000e9b1ad14c59d7ad05274"
COVER_TIP = "c" * 40
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))

RECORDED_STEPS = [
    ("Set up job", "success"), ("Checkout", "success"), ("Compute the verified-tree key", "success"),
    ("Verified-tree lookup (master push only)", "success"),
    ("Dockerfile covers every ACE.sln project", "success"), ("Setup .NET", "skipped"),
    ("Build", "skipped"), ("Test ACE.Server.Tests", "skipped"),
    ("Record the verified tree (pull_request only)", "skipped"),
    ("Report the verified-tree short-circuit", "success"), ("Post Checkout", "success"),
    ("Complete job", "success"),
]


def check_runs_fixture(*runs, name="build-test", other="content-apply"):
    """runs: (id, started_at) check runs named `name`; one in-progress run of the OTHER guarded
    check (`other`) is always mixed in, so a gate that reads the wrong check name is caught."""
    rows = [{"id": 900, "name": other, "head_sha": GATE_SHA, "status": "in_progress",
             "conclusion": None, "started_at": "2026-11-17T23:08:11Z"}]
    for rid, started in runs:
        rows.append({"id": rid, "name": name, "head_sha": GATE_SHA, "status": "completed",
                     "conclusion": "success", "started_at": started})
    return {"total_count": len(rows), "check_runs": rows}


def job_fixture(job_id, covered_step=None, sha=GATE_SHA, name="build-test", steps=None):
    st = list(RECORDED_STEPS) if steps is None else steps
    if covered_step is not None:
        st = st[:5] + [("Covered by newer master tip " + COVER_TIP, covered_step)] + st[5:]
    return {"id": job_id, "run_id": 1, "run_attempt": 1, "name": name, "head_sha": sha,
            "status": "completed", "conclusion": "success",
            "steps": [{"name": n, "status": "completed", "conclusion": c, "number": i + 1}
                      for i, (n, c) in enumerate(st)]}


FAKE_GH = """#!/usr/bin/env python3
import json, os, re, subprocess, sys
args = sys.argv[1:]
if not args or args[0] != "api":
    sys.exit("fake gh: only 'gh api' is supported")
path = args[1]
jq = args[args.index("--jq") + 1] if "--jq" in args else None
d = os.environ["FAKE_GH_DIR"]
if re.search(r"/commits/[0-9a-f]{40}/check-runs", path):
    f = os.path.join(d, "check-runs.json")
else:
    m = re.search(r"/actions/jobs/([0-9]+)$", path)
    f = os.path.join(d, "job-%s.json" % m.group(1)) if m else ""
if not f or not os.path.exists(f):
    sys.stderr.write("gh: Not Found (HTTP 404)\\n")
    sys.exit(1)
data = open(f).read()
if jq is None:
    sys.stdout.write(data)
    sys.exit(0)
p = subprocess.run(["jq", "-r", jq], input=data.encode(), stdout=subprocess.PIPE)
sys.stdout.write(p.stdout.decode())
sys.exit(p.returncode)
"""


def gate_scripts(workflows=None, step_name=None):
    workflows = GATE_WORKFLOWS if workflows is None else workflows
    GATE_STEP_ = GATE_STEP if step_name is None else step_name
    try:
        import yaml  # PyYAML: preinstalled on the GitHub ubuntu images build-test runs on
    except ImportError:
        check("gate: PyYAML is importable (needed to extract the gate steps)", False)
        return {}
    out = {}
    for wf in workflows:
        path = os.path.join(REPO_ROOT, ".github", "workflows", wf)
        if not os.path.exists(path):
            # The public mirror ships build-test.yml and this directory but not the deploy
            # workflows (tools/publish/public-allowlist.txt), so an absent gate is not a failure.
            print("SKIP  gate: %s is not in this tree" % wf)
            continue
        doc = yaml.safe_load(open(path))
        steps = [st for job in doc["jobs"].values() for st in job.get("steps", [])
                 if st.get("name") == GATE_STEP_]
        check("gate: %s has exactly one '%s' step" % (wf, GATE_STEP_), len(steps) == 1, repr(len(steps)))
        if steps:
            out[wf] = steps[0]
    return out


def run_gate(script, tmp, files):
    d = tempfile.mkdtemp(prefix="vt-gate-", dir=tmp)
    bindir = os.path.join(d, "bin")
    os.mkdir(bindir)
    gh = os.path.join(bindir, "gh")
    with open(gh, "w") as f:
        f.write(FAKE_GH.replace("#!/usr/bin/env python3", "#!" + sys.executable, 1))
    os.chmod(gh, 0o755)
    for name, obj in files.items():
        with open(os.path.join(d, name), "w") as f:
            f.write(obj if isinstance(obj, str) else json.dumps(obj))
    env = dict(os.environ, PATH=bindir + os.pathsep + os.environ.get("PATH", ""), FAKE_GH_DIR=d,
               GITHUB_REPOSITORY="o/r", GH_TOKEN="t", VT_SHA=GATE_SHA)
    p = subprocess.run(["bash", "-c", script], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       check=False)
    return p.returncode, p.stdout.decode() + p.stderr.decode()


def gate_case_table(check_name, other, rerun):
    """The refusal table shared by both guarded checks (build-test and content-apply): the gate
    step for `check_name` must treat the `other` check's rows as noise and name `rerun` as the remedy."""
    one = check_runs_fixture((501, "2026-11-17T23:08:11Z"), name=check_name, other=other)
    return [
        ("normal run (recorded steps, no covered step) -> allowed",
         {"check-runs.json": one, "job-501.json": job_fixture(501, name=check_name)}, 0, "ran on this sha"),
        ("covered-run step present but skipped (a normal run after this change) -> allowed",
         {"check-runs.json": one, "job-501.json": job_fixture(501, covered_step="skipped", name=check_name)}, 0, "ran on this sha"),
        ("covered while the tip's run was pending (same covered step) -> refused, naming the tip",
         {"check-runs.json": one, "job-501.json": job_fixture(501, covered_step="success", name=check_name,
                                                              steps=RECORDED_STEPS + [("Verified-tree pending check of the superseding tip (master push only)", "success")])},
         1, "was covered by newer tip " + COVER_TIP + ", not run on this sha"),
        ("covered run -> refused, naming the tip",
         {"check-runs.json": one, "job-501.json": job_fixture(501, covered_step="success", name=check_name)}, 1,
         "was covered by newer tip " + COVER_TIP + ", not run on this sha"),
        ("job steps unreadable (jobs API error) -> refused",
         {"check-runs.json": one}, 1, "could not read the steps"),
        ("job lookup 404 (deleted or retention-expired run) -> refused, naming the re-run remedy",
         {"check-runs.json": one}, 1, "A deleted or retention-expired run (HTTP 404) refuses too, as does a token without"
         " 'actions: read'. Remedy: re-run " + rerun + " on this sha, then deploy again."),
        ("unreadable job message names the re-run remedy",
         {"check-runs.json": one, "job-501.json": job_fixture(501, steps=[], name=check_name)}, 1,
         "Remedy: re-run " + rerun + " on this sha, then deploy again."),
        ("check runs unreadable -> refused", {}, 1, "could not read check runs"),
        ("no %s check run -> refused" % check_name,
         {"check-runs.json": check_runs_fixture(name=check_name, other=other)}, 1, "no " + check_name + " check run id"),
        ("job with an empty step list -> refused",
         {"check-runs.json": one, "job-501.json": job_fixture(501, steps=[], name=check_name)}, 1, "could not be read"),
        ("job for another sha -> refused",
         {"check-runs.json": one, "job-501.json": job_fixture(501, sha="d" * 40, name=check_name)}, 1, "could not be read"),
        ("job that is not %s -> refused" % check_name,
         {"check-runs.json": one, "job-501.json": job_fixture(501, name=other)}, 1, "could not be read"),
        ("job response is not JSON -> refused",
         {"check-runs.json": one, "job-501.json": "<html>oops</html>"}, 1, "could not be read"),
        ("newest run is a normal re-run of an older covered run -> allowed",
         {"check-runs.json": check_runs_fixture((501, "2026-11-17T23:08:11Z"), (502, "2026-11-17T23:40:00Z"), name=check_name, other=other),
          "job-501.json": job_fixture(501, covered_step="success", name=check_name), "job-502.json": job_fixture(502, name=check_name)},
         0, "ran on this sha"),
        ("newest run is covered although an older one was normal -> refused",
         {"check-runs.json": check_runs_fixture((502, "2026-11-17T23:40:00Z"), (501, "2026-11-17T23:08:11Z"), name=check_name, other=other),
          "job-501.json": job_fixture(501, name=check_name), "job-502.json": job_fixture(502, covered_step="success", name=check_name)},
         1, "was covered by newer tip"),
    ]


def gate_tests(tmp):
    if shutil.which("jq") is None:
        check("gate: jq is on PATH (the gate uses it; GitHub ubuntu images ship it)", False)
        return
    steps = gate_scripts()
    if not steps:
        print("SKIP  gate: no gate workflow in this tree; gate and marker-prefix checks skipped")
        return
    runs = {wf: st["run"] for wf, st in steps.items()}
    first = next(iter(runs.values()))
    check("gate: the step script is identical in every gate workflow",
          all(r == first for r in runs.values()), repr(sorted(runs)))
    # The producer side: build-test.yml's marker step must carry exactly the prefix the gates
    # match, and run only on a superseded (covered) verdict.
    import yaml
    m = re.search(r"^\s*prefix='([^']+)'$", first, re.M)
    prefix = m.group(1) if m else None
    bt = yaml.safe_load(open(os.path.join(REPO_ROOT, ".github", "workflows", "build-test.yml")))
    marker = [st for job in bt["jobs"].values() for st in job.get("steps", [])
              if prefix and str(st.get("name", "")).startswith(prefix)]
    check("gate: build-test.yml has exactly one step named with the gate's prefix %r" % prefix,
          len(marker) == 1 and marker[0].get("name") == prefix + " ${{ steps.verified.outputs.covering-sha }}",
          repr([st.get("name") for st in marker]))
    check("gate: that build-test step runs only when the verdict source is 'superseded'",
          len(marker) == 1 and marker[0].get("if") == "steps.verified.outputs.source == 'superseded'",
          repr([st.get("if") for st in marker]))
    envs = {wf: st.get("env", {}) for wf, st in steps.items()}
    check("gate: every gate passes VT_SHA from the resolved sha and a GH_TOKEN",
          all(e.get("VT_SHA") == "${{ steps.resolve.outputs.sha }}" and "GH_TOKEN" in e for e in envs.values()),
          repr(envs))

    cases = gate_case_table("build-test", "content-apply", "build-test")
    for wf in GATE_WORKFLOWS:
        if wf not in runs:
            continue
        for label, files, want_rc, want_text in cases:
            rc, out = run_gate(runs[wf], tmp, files)
            check("gate %s: %s" % (wf, label), rc == want_rc and want_text in out, repr((rc, out)))


TIP = "b" * 40
NAME = "verified-build-test-" + "1" * 40


def base_env(**kw):
    env = {"GITHUB_EVENT_NAME": "push", "GITHUB_REF": "refs/heads/master", "GITHUB_RUN_ATTEMPT": "1"}
    env.update(kw)
    return env


def combine_tests():
    covered = dict(VT_OWN_VERIFIED="false", VT_SUPERSEDED="true", VT_TIP_SHA=TIP, VT_TIP_NAMES=NAME,
                   VT_TIP_VERIFIED="true", VT_TIP_RUN_URL="https://x/runs/1", VT_TIP_MATCHED_NAME=NAME)
    r = superseded.combine(base_env(**covered))
    check("combine: superseded + tip verified -> covered",
          r["verified"] == "true" and r["source"] == "superseded" and r["covering-sha"] == TIP
          and r["run-url"] == "https://x/runs/1" and r["matched-name"] == NAME, repr(r))

    r = superseded.combine(base_env(VT_OWN_VERIFIED="", VT_SUPERSEDED="false"))
    check("combine: not superseded, own miss -> full run", r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(VT_OWN_VERIFIED="true", VT_OWN_RUN_URL="u", VT_OWN_MATCHED_NAME="n"))
    check("combine: own hit -> verified via own", r["verified"] == "true" and r["source"] == "own", repr(r))

    r = superseded.combine(base_env(**dict(covered, VT_TIP_VERIFIED="false")))
    check("combine: superseded + tip unverified -> full run", r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(**dict(covered, VT_TIP_VERIFIED="")))
    check("combine: superseded + tip lookup did not run / errored (empty) -> full run",
          r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(**dict(covered, VT_TIP_MATCHED_NAME="verified-build-test-" + "2" * 40)))
    check("combine: tip lookup matched a name this run did not compute -> full run",
          r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(**dict(covered, VT_TIP_SHA="")))
    check("combine: empty tip sha -> full run", r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(**dict(covered, VT_TIP_RUN_URL="")))
    check("combine: no producing run url -> full run", r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(**dict(covered, VT_SUPERSEDED="")))
    check("combine: detect failed (empty superseded) -> full run", r["verified"] == "false", repr(r))

    r = superseded.combine(dict(covered, GITHUB_EVENT_NAME="pull_request", GITHUB_REF="refs/pull/1/merge"))
    check("combine: pull_request run never short-circuits", r["verified"] == "false", repr(r))

    r = superseded.combine(dict(covered, GITHUB_EVENT_NAME="push", GITHUB_REF="refs/heads/other"))
    check("combine: push to another branch never short-circuits", r["verified"] == "false", repr(r))

    r = superseded.combine(base_env(**dict(covered, GITHUB_RUN_ATTEMPT="2")))
    check("combine: re-run (attempt 2) never takes superseded coverage", r["verified"] == "false", repr(r))
    env = base_env(**covered)
    del env["GITHUB_RUN_ATTEMPT"]
    r = superseded.combine(env)
    check("combine: absent run attempt -> full run", r["verified"] == "false", repr(r))
    r = superseded.combine(base_env(**dict(covered, GITHUB_RUN_ATTEMPT="1x")))
    check("combine: malformed run attempt -> full run", r["verified"] == "false", repr(r))
    r = superseded.combine(base_env(VT_OWN_VERIFIED="true", VT_OWN_RUN_URL="u", VT_OWN_MATCHED_NAME="n",
                                    GITHUB_RUN_ATTEMPT="2"))
    check("combine: own-key hit is unaffected by the run attempt (pre-existing behaviour)",
          r["verified"] == "true" and r["source"] == "own", repr(r))

    # CLI: verified written LAST, summary written on a superseded hit, exit 0.
    tmp = tempfile.mkdtemp(prefix="vt-combine-")
    try:
        out = os.path.join(tmp, "out")
        summ = os.path.join(tmp, "summary")
        env = dict(os.environ, **base_env(**covered))
        env.update(GITHUB_OUTPUT=out, GITHUB_STEP_SUMMARY=summ, GITHUB_SHA="a" * 40, VT_JOB_LABEL="build-test")
        p = subprocess.run([sys.executable, os.path.join(HERE, "superseded.py"), "combine"], env=env,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        lines = open(out).read().splitlines()
        summary = open(summ).read() if os.path.exists(summ) else ""
        check("combine CLI: exit 0, verified=true written last",
              p.returncode == 0 and lines[-1] == "verified=true", repr((p.returncode, lines)))
        check("combine CLI: summary names the covering tip and the reused run",
              TIP in summary and "https://x/runs/1" in summary and "NOT separately verified" in summary,
              summary)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ---- design step 11: covered while the tip's full push run is pending --------------------------

OWN_RUN = "777"


def tip_run(**kw):
    r = {"id": 4242, "head_sha": TIP, "event": "push", "head_branch": "master", "path": WF,
         "run_attempt": 1, "status": "queued", "conclusion": None, "html_url": "https://x/runs/4242",
         "repository": {"full_name": REPO}, "head_repository": {"full_name": REPO}}
    r.update(kw)
    return r


def pending_env(**kw):
    env = {"VT_TIP_SHA": TIP, "GITHUB_REPOSITORY": REPO, "VT_WORKFLOW_PATH": WF, "GITHUB_RUN_ID": OWN_RUN}
    env.update(kw)
    return env


def fake_runs(*runs, total=None):
    body = {"total_count": len(runs) if total is None else total, "workflow_runs": list(runs)}
    seen = []

    def get(path):
        seen.append(path)
        return body
    get.seen = seen
    return get


PENDING_COVERED = dict(VT_OWN_VERIFIED="false", VT_SUPERSEDED="true", VT_TIP_SHA=TIP, VT_TIP_NAMES=NAME,
                       VT_TIP_VERIFIED="false", VT_TIP_PENDING="true", VT_TIP_PENDING_SHA=TIP,
                       VT_TIP_PENDING_RUN_URL="https://x/runs/4242")


def pending_cases(mod, ck):
    """The step-11 rule, against module `mod` (superseded, or a mutant of it). ck(label, cond, detail)."""
    def pend(*runs, total=None, **env):
        return mod.pending(pending_env(**env), fake_runs(*runs, total=total))

    get = fake_runs(tip_run())
    r = mod.pending(pending_env(), get)
    ck("pending: one queued first-attempt master push run of this workflow -> pending",
       r["pending"] == "true" and r["run-url"] == "https://x/runs/4242" and r["tip-sha"] == TIP, repr(r))
    q = get.seen[0] if get.seen else ""
    ck("pending: the query names the workflow file, the tip sha, event=push and branch=master",
       q.startswith("/repos/o/r/actions/workflows/build-test.yml/runs?") and ("head_sha=" + TIP) in q
       and "event=push" in q and "branch=master" in q, q)
    r = pend(tip_run(status="in_progress"))
    ck("pending: in_progress -> pending", r["pending"] == "true", repr(r))

    # content-apply-ci.yml asks about ITS OWN run on the tip, never build-test's.
    get = fake_runs(tip_run(path=CA_WF))
    r = mod.pending(pending_env(VT_WORKFLOW_PATH=CA_WF), get)
    q = get.seen[0] if get.seen else ""
    ck("pending (content-apply): one queued content-apply-ci push run on the tip -> pending",
       r["pending"] == "true" and r["run-url"] == "https://x/runs/4242", repr(r))
    ck("pending (content-apply): the query names content-apply-ci.yml, not build-test.yml",
       q.startswith("/repos/o/r/actions/workflows/content-apply-ci.yml/runs?") and ("head_sha=" + TIP) in q
       and "event=push" in q and "branch=master" in q, q)
    r = pend(tip_run(path=WF), VT_WORKFLOW_PATH=CA_WF)
    ck("pending (content-apply): only a build-test run is pending for the tip -> not pending",
       r["pending"] == "false", repr(r))
    r = pend(tip_run(path=CA_WF), VT_WORKFLOW_PATH=WF)
    ck("pending (build-test): only a content-apply-ci run is pending for the tip -> not pending",
       r["pending"] == "false", repr(r))
    r = pend(tip_run(path=CA_WF, status="completed", conclusion="cancelled"), VT_WORKFLOW_PATH=CA_WF)
    ck("pending (content-apply): tip run cancelled -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(path=CA_WF, run_attempt=2), VT_WORKFLOW_PATH=CA_WF)
    ck("pending (content-apply): tip run is a re-run -> not pending", r["pending"] == "false", repr(r))
    r = pend(VT_WORKFLOW_PATH=CA_WF)
    ck("pending (content-apply): tip run not registered yet -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(path=CA_WF), tip_run(path=CA_WF, id=4243), VT_WORKFLOW_PATH=CA_WF)
    ck("pending (content-apply): two tip runs (ambiguous) -> not pending", r["pending"] == "false", repr(r))
    for status, conclusion, label in (("completed", "failure", "tip run failed"),
                                      ("completed", "cancelled", "tip run cancelled"),
                                      ("completed", "timed_out", "tip run timed out"),
                                      ("completed", "success", "tip run passed but left no marker (e.g. itself covered)"),
                                      ("waiting", None, "status waiting"), ("requested", None, "status requested"),
                                      ("pending", None, "status pending"), ("", None, "status empty")):
        r = pend(tip_run(status=status, conclusion=conclusion))
        ck("pending: %s -> not pending (full run)" % label, r["pending"] == "false", repr(r))
    r = pend()
    ck("pending: tip run not registered yet (race: push landed seconds ago) -> not pending, no wait",
       r["pending"] == "false" and "not wait" in r["reason"], repr(r))
    r = pend(tip_run(), tip_run(id=4243))
    ck("pending: two runs for the tip -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(run_attempt=2))
    ck("pending: a re-run attempt (2) -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(run_attempt="1"))
    ck("pending: run_attempt not an int 1 -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(event="pull_request"))
    ck("pending: a pull_request run (may be narrow) -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(event="workflow_dispatch"))
    ck("pending: a workflow_dispatch run -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(head_branch="train/x"))
    ck("pending: a run on another branch -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(path=".github/workflows/content-lint.yml"))
    ck("pending: another workflow's run -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(head_repository={"full_name": "fork/r"}))
    ck("pending: fork head repository -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(repository={"full_name": "other/r"}))
    ck("pending: another repository -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(id=int(OWN_RUN)))
    ck("pending: the run found is this very run -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(head_sha="d" * 40))
    ck("pending: the run is for another sha (API filter not honoured) -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(html_url=""))
    ck("pending: run without an html_url -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(), total=3)
    ck("pending: incomplete listing (total_count > rows) -> not pending", r["pending"] == "false", repr(r))
    r = mod.pending(pending_env(), lambda path: {"oops": 1})
    ck("pending: malformed listing -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(), VT_TIP_SHA="")
    ck("pending: empty tip sha -> not pending, no API call", r["pending"] == "false", repr(r))
    r = pend(tip_run(), VT_WORKFLOW_PATH="")
    ck("pending: empty workflow path -> not pending", r["pending"] == "false", repr(r))
    r = pend(tip_run(), GITHUB_REPOSITORY="")
    ck("pending: missing repository -> not pending", r["pending"] == "false", repr(r))

    c = mod.combine(base_env(**PENDING_COVERED))
    ck("combine: superseded + tip unverified + tip run pending -> covered (basis pending)",
       c["verified"] == "true" and c["source"] == "superseded" and c.get("basis") == "pending"
       and c["covering-sha"] == TIP and c["run-url"] == "https://x/runs/4242", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_TIP_PENDING="false")))
    ck("combine: tip run not pending (failed / cancelled / unregistered) -> full run", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_TIP_PENDING="")))
    ck("combine: pending check did not run or errored (empty) -> full run", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_TIP_PENDING_SHA="d" * 40)))
    ck("combine: pending check was for another sha -> full run", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_TIP_PENDING_RUN_URL="")))
    ck("combine: pending without a run url -> full run", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_SUPERSEDED="false")))
    ck("combine: tip does not descend (not superseded) + pending -> full run", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, GITHUB_RUN_ATTEMPT="2")))
    ck("combine: re-run (attempt 2) + pending -> full run", c["verified"] == "false", repr(c))
    c = mod.combine(dict(PENDING_COVERED, GITHUB_EVENT_NAME="pull_request", GITHUB_REF="refs/pull/1/merge"))
    ck("combine: pull_request + pending -> never short-circuits", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_TIP_VERIFIED="true", VT_TIP_MATCHED_NAME="verified-build-test-" + "2" * 40,
                                    VT_TIP_RUN_URL="https://x/runs/1")))
    ck("combine: tip lookup verified a wrong name -> full run, no fallback to pending", c["verified"] == "false", repr(c))
    c = mod.combine(base_env(**dict(PENDING_COVERED, VT_TIP_VERIFIED="true", VT_TIP_MATCHED_NAME=NAME,
                                    VT_TIP_RUN_URL="https://x/runs/1")))
    ck("combine: tip verified and pending -> covered on the verified basis",
       c["verified"] == "true" and c.get("basis") == "verified" and c["run-url"] == "https://x/runs/1", repr(c))


# Each mutant is one edit to superseded.py's source; pending_cases against the mutated module must fail.
PENDING_MUTANTS = [
    ("covered even when the tip's run failed (completed counts as pending)",
     'PENDING_STATUSES = ("queued", "in_progress")', 'PENDING_STATUSES = ("queued", "in_progress", "completed")'),
    ("any non-completed status counts as pending",
     "    if status not in PENDING_STATUSES:\n", "    if status == \"completed\":\n"),
    ("a re-run attempt counts", '    if run.get("run_attempt") != 1:\n', "    if False:\n"),
    ("a pull_request run counts", '    if run.get("event") != "push":\n', "    if False:\n"),
    ("a run on another branch counts", '    if run.get("head_branch") != "master":\n', "    if False:\n"),
    ("another workflow's run counts", '    if run.get("path") != workflow_path:\n', "    if False:\n"),
    ("a fork run counts", "    if run_repo != repo.lower() or head_repo != repo.lower():\n", "    if False:\n"),
    ("this very run counts", '    if str(run.get("id")) == own_run_id:\n', "    if False:\n"),
    ("a run for another sha counts", '    if (run.get("head_sha") or "").lower() != tip:\n', "    if False:\n"),
    ("two runs: the first one is used", "    if len(runs) != 1:\n", "    if False:\n"),
    ("the query drops event=push", "&event=push&branch=master", "&branch=master"),
    ("the query hard-codes build-test.yml instead of the caller's workflow file",
     "            % (repo, wf_file, tip))\n", "            % (repo, \"build-test.yml\", tip))\n"),
    ("combine ignores the pending check's sha", "        if pending_sha != tip:\n", "        if False:\n"),
    ("combine accepts pending without a run url", "        if not pending_url:\n", "        if False:\n"),
    ("combine falls back to pending when the tip lookup verified a wrong name",
     '        res["reason"] = "tip lookup matched %r, which is not one of the tip key names %r" % (matched, expected)\n        return res\n',
     '        if env.get("VT_TIP_PENDING") == "true" and env.get("VT_TIP_PENDING_RUN_URL"):\n'
     '            res.update({"verified": "true", "source": "superseded", "basis": "pending", "covering-sha": tip})\n'
     '        res["reason"] = "tip lookup matched %r, which is not one of the tip key names %r" % (matched, expected)\n        return res\n'),
    ("combine labels a pending hit with source pending (the covered step and the gates would miss it)",
     '"verified": "true", "source": "superseded", "basis": "pending"', '"verified": "true", "source": "pending", "basis": "pending"'),
]


def pending_mutation_tests():
    import importlib.util
    src = open(os.path.join(HERE, "superseded.py"), encoding="utf-8").read()
    for label, old, new in PENDING_MUTANTS:
        if src.count(old) != 1:
            check("mutant applies exactly once: %s" % label, False, "occurrences=%d" % src.count(old))
            continue
        spec = importlib.util.spec_from_loader("superseded_mutant", loader=None)
        mod = importlib.util.module_from_spec(spec)
        exec(compile(src.replace(old, new, 1), "superseded_mutant.py", "exec"), mod.__dict__)
        fails = []
        try:
            pending_cases(mod, lambda lbl, cond, detail="": fails.append(lbl) if not cond else None)
        except Exception as e:  # a crash also kills the mutant
            fails.append("crash %r" % (e,))
        check("mutant is killed: %s" % label, bool(fails), "no case failed")


def pending_cli_tests():
    """The pending and combine CLIs end to end: a fake REST API, outputs last, the summary, and the chain
    combine -> build-test's covered-step condition -> deploy gate refusal."""
    tmp = tempfile.mkdtemp(prefix="vt-pending-")
    state = {"body": {"total_count": 1, "workflow_runs": [tip_run()]}, "code": 200}

    class H(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def do_GET(self):
            from urllib.parse import urlparse
            if state["code"] != 200 or urlparse(self.path).path != "/repos/%s/actions/workflows/build-test.yml/runs" % REPO:
                self.send_response(state["code"] if state["code"] != 200 else 404)
                self.end_headers()
                return
            data = json.dumps(state["body"]).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

    srv = http.server.HTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    url = "http://127.0.0.1:%d" % srv.server_address[1]

    def cli(token="t"):
        out = os.path.join(tmp, "pending.out")
        if os.path.exists(out):
            os.remove(out)
        env = dict(os.environ, GITHUB_TOKEN=token, GITHUB_REPOSITORY=REPO, GITHUB_API_URL=url,
                   GITHUB_RUN_ID=OWN_RUN, GITHUB_OUTPUT=out, VT_TIP_SHA=TIP, VT_WORKFLOW_PATH=WF)
        p = subprocess.run([sys.executable, os.path.join(HERE, "superseded.py"), "pending"], env=env,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        raw = open(out).read() if os.path.exists(out) else ""
        return p.returncode, raw.splitlines(), dict(l.split("=", 1) for l in raw.splitlines() if "=" in l)

    try:
        rc, lines, r = cli()
        check("pending CLI: queued tip run -> exit 0, pending=true written last, url and tip sha output",
              rc == 0 and lines and lines[-1] == "pending=true" and r.get("run-url") == "https://x/runs/4242"
              and r.get("tip-sha") == TIP, repr((rc, lines)))
        state["body"] = {"total_count": 1, "workflow_runs": [tip_run(status="completed", conclusion="failure")]}
        rc, lines, r = cli()
        check("pending CLI: failed tip run -> exit 0, pending=false", rc == 0 and lines[-1:] == ["pending=false"], repr((rc, lines)))
        state["body"] = {"total_count": 0, "workflow_runs": []}
        rc, lines, r = cli()
        check("pending CLI: tip run not registered -> exit 0, pending=false", rc == 0 and lines[-1:] == ["pending=false"], repr((rc, lines)))
        state["code"] = 500
        rc, lines, r = cli()
        check("pending CLI: HTTP 500 -> exit 0, pending=false", rc == 0 and lines[-1:] == ["pending=false"], repr((rc, lines)))
        state["code"] = 200
        rc, lines, r = cli(token="")
        check("pending CLI: no token -> exit 0, pending=false", rc == 0 and lines[-1:] == ["pending=false"], repr((rc, lines)))
    finally:
        srv.shutdown()
        srv.server_close()
    rc, lines, r = cli()
    check("pending CLI: API unreachable -> exit 0, pending=false", rc == 0 and lines[-1:] == ["pending=false"], repr((rc, lines)))

    try:
        out = os.path.join(tmp, "combine.out")
        summ = os.path.join(tmp, "summary")
        env = dict(os.environ, **base_env(**PENDING_COVERED))
        env.update(GITHUB_OUTPUT=out, GITHUB_STEP_SUMMARY=summ, GITHUB_SHA="a" * 40, VT_JOB_LABEL="build-test")
        p = subprocess.run([sys.executable, os.path.join(HERE, "superseded.py"), "combine"], env=env,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        lines = open(out).read().splitlines()
        outs = dict(l.split("=", 1) for l in lines)
        summary = open(summ).read() if os.path.exists(summ) else ""
        check("combine CLI (pending): exit 0, verified=true last, source superseded, basis pending",
              p.returncode == 0 and lines[-1] == "verified=true" and outs.get("source") == "superseded"
              and outs.get("basis") == "pending", repr((p.returncode, lines)))
        check("combine CLI (pending): summary names the tip, the pending run, and that it had not passed",
              TIP in summary and "https://x/runs/4242" in summary and "had NOT passed yet" in summary
              and "not deployable" in summary, summary)
        check("combine CLI (pending, build-test): summary names build-test's audit range",
              "this commit is inside its audit range." in summary, summary)

        # The chain to the deploy gates: build-test's covered step runs on exactly this verdict, and a job
        # carrying that step is refused by every gate.
        try:
            import yaml
        except ImportError:
            check("chain: PyYAML is importable", False)
            return
        bt = yaml.safe_load(open(os.path.join(REPO_ROOT, ".github", "workflows", "build-test.yml")))
        marker = [st for job in bt["jobs"].values() for st in job.get("steps", [])
                  if str(st.get("name", "")).startswith("Covered by newer master tip")]
        cond = marker[0].get("if") if len(marker) == 1 else None
        m = re.match(r"^steps\.verified\.outputs\.source == '([a-z]+)'$", cond or "")
        check("chain: build-test's covered step runs on this pending verdict (source %r)" % outs.get("source"),
              bool(m) and m.group(1) == outs.get("source"), repr(cond))
        steps = gate_scripts()
        if shutil.which("jq") is None or not steps:
            print("SKIP  chain: no gate workflow or no jq in this tree")
            return
        job = job_fixture(501, steps=[("Set up job", "success"), ("Checkout", "success"),
                                      ("Verified-tree pending check of the superseding tip (master push only)", "success"),
                                      ("Verified-tree verdict (master push only)", "success"),
                                      ("Covered by newer master tip " + outs.get("covering-sha", ""), "success"),
                                      ("Build", "skipped"), ("Report the verified-tree short-circuit", "success")])
        for wf, st in steps.items():
            rc, gout = run_gate(st["run"], tmp, {"check-runs.json": check_runs_fixture((501, "2026-11-18T10:00:00Z")),
                                                 "job-501.json": job})
            check("chain %s: a sha covered while its tip was pending is refused" % wf,
                  rc == 1 and ("was covered by newer tip " + TIP) in gout, repr((rc, gout)))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ---- content-apply-ci.yml: the same superseded/pending coverage, its gates and its tip key --------

CA_GATE_WORKFLOWS = ["deploy-prod.yml", "deploy-content-prod.yml"]
CA_GATE_STEP = "Refuse a content-apply covered by a newer master tip"
CA_REQUIRE_STEP = "Require a green content-apply run"
COVERED_PREFIX = "Covered by newer master tip"


def load_yaml(rel):
    import yaml
    return yaml.safe_load(open(os.path.join(REPO_ROOT, rel), encoding="utf-8"))


def ca_gate_tests(tmp):
    """The content-apply covered refusal in deploy-prod.yml and deploy-content-prod.yml, run for real."""
    if shutil.which("jq") is None:
        check("ca gate: jq is on PATH", False)
        return
    try:
        import yaml  # noqa: F401
    except ImportError:
        check("ca gate: PyYAML is importable", False)
        return
    steps = gate_scripts(CA_GATE_WORKFLOWS, CA_GATE_STEP)
    if not steps:
        print("SKIP  ca gate: no content-apply gate workflow in this tree")
        return
    check("ca gate: both content-apply gate workflows carry the refusal step",
          sorted(steps) == sorted(CA_GATE_WORKFLOWS), repr(sorted(steps)))
    runs = {wf: st["run"] for wf, st in steps.items()}
    first = next(iter(runs.values()))
    check("ca gate: the step script is identical in both gate workflows",
          all(r == first for r in runs.values()), repr(sorted(runs)))
    # Derived, not copied by hand: the content-apply script is the build-test one with only the
    # check name and the remedy retargeted. Any drift in the decision logic fails here.
    bt = gate_scripts(["deploy-prod.yml"], GATE_STEP).get("deploy-prod.yml")
    if bt is not None:
        derived = (bt["run"].replace('"build-test"', '"content-apply"')
                   .replace("re-run build-test on this sha", "re-run content-apply-ci on this sha")
                   .replace("build-test job", "content-apply job").replace("its build-test was", "its content-apply was")
                   .replace("no build-test check run", "no content-apply check run")
                   .replace("as a build-test job", "as a content-apply job")
                   .replace("::error::build-test for", "::error::content-apply for"))
        check("ca gate: the script is deploy-prod.yml's build-test refusal retargeted at content-apply, nothing else",
              derived == first, "scripts differ")
    for wf, st in steps.items():
        env = st.get("env", {})
        check("ca gate %s: VT_SHA is the manifest sha the content-apply gate checks, and GH_TOKEN is set" % wf,
              env.get("VT_SHA") == "${{ steps.manifest.outputs.sha }}" and "GH_TOKEN" in env, repr(env))
        doc = load_yaml(os.path.join(".github", "workflows", wf))
        gate_job = [j for j in doc["jobs"].values() if CA_GATE_STEP in [x.get("name") for x in j.get("steps", [])]]
        # The effective permissions of the job holding the refusal: its own block, when it has one,
        # replaces the workflow-level block entirely.
        perms = (gate_job[0].get("permissions") if gate_job and "permissions" in gate_job[0]
                 else doc.get("permissions")) or {}
        check("ca gate %s: the gate job's effective permissions grant actions: read and checks: read" % wf,
              perms.get("actions") == "read" and perms.get("checks") == "read", repr(perms))
        if wf == "deploy-content-prod.yml":
            top = doc.get("permissions") or {}
            others = {n: j.get("permissions") for n, j in doc["jobs"].items() if not gate_job or j is not gate_job[0]}
            check("ca gate %s: actions: read is on the gate job only (not workflow-level, not on apply/record)" % wf,
                  "actions" not in top and all("actions" not in (pj or {}) for pj in others.values())
                  and (gate_job and gate_job[0].get("permissions", {}).get("contents") == "read"),
                  repr((top, others)))
        for job in doc["jobs"].values():
            names = [x.get("name") for x in job.get("steps", [])]
            if CA_GATE_STEP in names:
                req = [x for x in job["steps"] if x.get("name") == CA_REQUIRE_STEP]
                check("ca gate %s: the refusal sits in the same job, right after '%s', under the same if" % (wf, CA_REQUIRE_STEP),
                      len(req) == 1 and names.index(CA_GATE_STEP) == names.index(CA_REQUIRE_STEP) + 1
                      and st.get("if") == req[0].get("if"), repr((names, st.get("if"), req and req[0].get("if"))))
    caller = load_yaml(os.path.join(".github", "workflows", "deploy-all-prod.yml"))
    check("ca gate: deploy-all-prod.yml (calls deploy-prod.yml) grants actions: read",
          (caller.get("permissions") or {}).get("actions") == "read", repr(caller.get("permissions")))
    # Producer side: content-apply-ci.yml's covered step carries the gates' prefix and runs only
    # on a superseded verdict.
    m = re.search(r"^\s*prefix='([^']+)'$", first, re.M)
    prefix = m.group(1) if m else None
    check("ca gate: the gate's prefix is %r" % COVERED_PREFIX, prefix == COVERED_PREFIX, repr(prefix))
    ca = load_yaml(CA_WF)
    marker = [x for job in ca["jobs"].values() for x in job.get("steps", [])
              if prefix and str(x.get("name", "")).startswith(prefix)]
    check("ca gate: content-apply-ci.yml has exactly one covered step, named with the prefix and the covering sha",
          len(marker) == 1 and marker[0].get("name") == prefix + " ${{ steps.verified.outputs.covering-sha }}",
          repr([x.get("name") for x in marker]))
    check("ca gate: that step runs only when the verdict source is 'superseded'",
          len(marker) == 1 and marker[0].get("if") == "steps.verified.outputs.source == 'superseded'",
          repr([x.get("if") for x in marker]))
    for wf in CA_GATE_WORKFLOWS:
        if wf not in runs:
            continue
        for label, files, want_rc, want_text in gate_case_table("content-apply", "build-test", "content-apply-ci"):
            rc, out = run_gate(runs[wf], tmp, files)
            check("ca gate %s: %s" % (wf, label), rc == want_rc and want_text in out, repr((rc, out)))


def ca_steps(doc):
    return doc["jobs"]["content-apply"]["steps"]


def ca_wiring_problems(text):
    """Every way content-apply-ci.yml's coverage wiring can be wrong, as a list of problems (empty =
    correct). Takes the workflow TEXT so the mutation pass below can feed it edited copies."""
    import yaml
    probs = []
    try:
        steps = ca_steps(yaml.safe_load(text))
    except Exception as e:
        return ["unparsable: %r" % (e,)]
    by_id = {x.get("id"): x for x in steps if x.get("id")}
    idx = {x.get("id"): i for i, x in enumerate(steps) if x.get("id")}
    push = "github.event_name == 'push' && github.ref == 'refs/heads/master'"

    def want(cond, msg):
        if not cond:
            probs.append(msg)

    for sid in ("loader-diff", "vt-tip", "vt-key", "verified-own", "verified-tip", "verified-pending", "verified"):
        want(sid in by_id, "missing step id %s" % sid)
    if probs:
        return probs
    w = lambda sid: by_id[sid].get("with") or {}
    want(idx["loader-diff"] < idx["vt-tip"] < idx["vt-key"] < idx["verified-own"] < idx["verified-tip"]
         < idx["verified-pending"] < idx["verified"], "step order")
    want(w("vt-tip").get("mode") == "superseded" and by_id["vt-tip"].get("if") == push, "vt-tip mode/if")
    env = by_id["vt-key"].get("env") or {}
    want(env.get("VT_FIDELITY_REQUIRED") == "${{ steps.loader-diff.outputs.run }}"
         and env.get("VT_SUPERSEDED") == "${{ steps.vt-tip.outputs.superseded }}"
         and env.get("VT_TIP_SHA") == "${{ steps.vt-tip.outputs.tip-sha }}", "vt-key env")
    want(w("verified-own").get("mode") == "lookup" and w("verified-own").get("names") == "${{ steps.vt-key.outputs.names }}"
         and w("verified-own").get("workflow-path") == CA_WF, "own lookup")
    want(w("verified-tip").get("mode") == "lookup" and w("verified-tip").get("names") == "${{ steps.vt-key.outputs.tip-name }}"
         and w("verified-tip").get("workflow-path") == CA_WF, "tip lookup")
    want(by_id["verified-tip"].get("if") == push + " && steps.verified-own.outputs.verified != 'true' && "
         "steps.vt-key.outputs.tip-name != ''", "tip lookup if")
    want(w("verified-pending").get("mode") == "pending" and w("verified-pending").get("workflow-path") == CA_WF
         and w("verified-pending").get("tip-sha") == "${{ steps.vt-tip.outputs.tip-sha }}", "pending check inputs")
    want(by_id["verified-pending"].get("if") == push + " && steps.loader-diff.outputs.run == 'false' && "
         "steps.verified-own.outputs.verified != 'true' && steps.vt-key.outputs.tip-name != '' && "
         "steps.verified-tip.outputs.verified != 'true'", "pending check if (must require loader-diff run == 'false')")
    expect = {
        "mode": "combine", "job-label": "content-apply",
        "own-verified": "${{ steps.verified-own.outputs.verified }}",
        "own-run-url": "${{ steps.verified-own.outputs.run-url }}",
        "own-matched-name": "${{ steps.verified-own.outputs.matched-name }}",
        "superseded": "${{ steps.vt-tip.outputs.superseded }}",
        "tip-sha": "${{ steps.vt-tip.outputs.tip-sha }}",
        "tip-names": "${{ steps.vt-key.outputs.tip-name }}",
        "tip-verified": "${{ steps.verified-tip.outputs.verified }}",
        "tip-run-url": "${{ steps.verified-tip.outputs.run-url }}",
        "tip-matched-name": "${{ steps.verified-tip.outputs.matched-name }}",
        "tip-pending": "${{ steps.verified-pending.outputs.pending }}",
        "tip-pending-sha": "${{ steps.verified-pending.outputs.pending-tip-sha }}",
        "tip-pending-run-url": "${{ steps.verified-pending.outputs.pending-run-url }}",
    }
    want(w("verified") == expect, "combine inputs %r" % (w("verified"),))
    want(by_id["verified"].get("if") == push, "combine if")
    covered = [i for i, x in enumerate(steps) if str(x.get("name", "")).startswith(COVERED_PREFIX)]
    want(len(covered) == 1 and covered[0] > idx["verified"]
         and steps[covered[0]].get("if") == "steps.verified.outputs.source == 'superseded'", "covered step")
    # Every heavy step after the verdict is gated on it; no step between the verdict and the first
    # heavy step may run unconditionally except the covered marker.
    heavy = [x for x in steps if x.get("name") in (
        "Start MySQL 8.0 (preinstalled) and set root credentials", "Verify MySQL is 8.0.x", "Import world database",
        "Generate the manifest from this ref", "Apply the manifest (first pass)",
        "Apply the manifest (second pass, idempotency, full)",
        "Apply the changed units (second pass, idempotency, PR-scoped)", "Setup .NET",
        "Bulk weenie loader fidelity (retail + fork content)")]
    want(len(heavy) == 9, "heavy step count %d" % len(heavy))
    want(all(str(x.get("if", "")).startswith("steps.verified.outputs.verified != 'true'") for x in heavy),
         "a heavy step is not gated on the verdict")
    records = [x for x in steps if (x.get("with") or {}).get("mode") == "record"]
    want(len(records) == 2 and all("steps.verified.outputs.verified != 'true'" in str(x.get("if", "")) for x in records),
         "a record step can record on a covered run")
    return probs


# One edit each to content-apply-ci.yml; ca_wiring_problems must report every one.
CA_WIRING_MUTANTS = [
    ("pending check without the no-fidelity guard",
     " && steps.loader-diff.outputs.run == 'false' && steps.verified-own", " && steps.verified-own"),
    ("pending check asks about build-test's runs",
     "          mode: pending\n          tip-sha: ${{ steps.vt-tip.outputs.tip-sha }}\n          workflow-path: .github/workflows/content-apply-ci.yml\n",
     "          mode: pending\n          tip-sha: ${{ steps.vt-tip.outputs.tip-sha }}\n          workflow-path: .github/workflows/build-test.yml\n"),
    ("tip lookup accepts build-test markers",
     "          names: ${{ steps.vt-key.outputs.tip-name }}\n          workflow-path: .github/workflows/content-apply-ci.yml\n",
     "          names: ${{ steps.vt-key.outputs.tip-name }}\n          workflow-path: .github/workflows/build-test.yml\n"),
    ("combine reads tip-pending from the tip lookup",
     "tip-pending: ${{ steps.verified-pending.outputs.pending }}", "tip-pending: ${{ steps.verified-tip.outputs.verified }}"),
    ("combine reads the own verdict from the tip lookup",
     "own-verified: ${{ steps.verified-own.outputs.verified }}", "own-verified: ${{ steps.verified-tip.outputs.verified }}"),
    ("covered step runs on an own hit instead", "        if: steps.verified.outputs.source == 'superseded'\n",
     "        if: steps.verified.outputs.source == 'own'\n"),
    ("tip key reads this commit's inputs, not the tip's", "VT_TIP_SHA: ${{ steps.vt-tip.outputs.tip-sha }}\n        run: |\n          set -euo",
     "VT_TIP_SHA: ${{ github.sha }}\n        run: |\n          set -euo"),
    ("first apply not gated on the verdict",
     "        id: apply-1\n        if: steps.verified.outputs.verified != 'true'\n",
     "        id: apply-1\n"),
    ("record step can record on a covered run",
     "          success() &&\n          steps.verified.outputs.verified != 'true' &&\n          steps.apply-1.outcome == 'success' &&\n          steps.apply-2-full.outcome == 'success' &&\n          ((",
     "          success() &&\n          steps.apply-1.outcome == 'success' &&\n          steps.apply-2-full.outcome == 'success' &&\n          (("),
]


def ca_wiring_tests():
    try:
        import yaml  # noqa: F401
    except ImportError:
        check("ca wiring: PyYAML is importable", False)
        return
    text = open(os.path.join(REPO_ROOT, CA_WF), encoding="utf-8").read()
    probs = ca_wiring_problems(text)
    check("ca wiring: content-apply-ci.yml's coverage wiring is exactly as designed", not probs, repr(probs))
    for label, old, new in CA_WIRING_MUTANTS:
        if text.count(old) != 1:
            check("ca wiring mutant applies exactly once: %s" % label, False, "occurrences=%d" % text.count(old))
            continue
        check("ca wiring mutant is killed: %s" % label, bool(ca_wiring_problems(text.replace(old, new, 1))),
              "no problem reported")


def ca_key_script():
    import yaml
    steps = ca_steps(yaml.safe_load(open(os.path.join(REPO_ROOT, CA_WF), encoding="utf-8")))
    return [x for x in steps if x.get("id") == "vt-key"][0]["run"]


def run_key_script(script, work, tmp, block=None, **env):
    rt = tempfile.mkdtemp(prefix="vt-rt-", dir=tmp)
    if block and block.startswith("ro:"):
        # A stale, read-only file where the script writes: the redirect fails but the old file
        # still reads back, so only an explicit failure check notices.
        ro = os.path.join(rt, block[3:])
        with open(ro, "w") as f:
            f.write("stale\n")
        os.chmod(ro, 0o444)
    elif block:
        # A directory where the script writes a file: that redirect fails.
        os.mkdir(os.path.join(rt, block))
    out = os.path.join(rt, "out")
    e = dict(os.environ, RUNNER_TEMP=rt, GITHUB_OUTPUT=out)
    for k in ("VT_FIDELITY_REQUIRED", "VT_SUPERSEDED", "VT_TIP_SHA"):
        e.pop(k, None)
    e.update(env)
    p = subprocess.run(["bash", "-c", script], cwd=work, env=e, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    raw = open(out).read() if os.path.exists(out) else ""
    return p.returncode, dict(l.split("=", 1) for l in raw.splitlines() if "=" in l), p.stdout.decode() + p.stderr.decode()


def ca_tip_key_cases(script, work, tmp, c, t, ck):
    """The superseding tip's key, computed from C's checkout, must equal the key T's own run computes."""
    rc, own_t, _ = run_key_script(script, work, tmp, VT_FIDELITY_REQUIRED="false")  # HEAD is T here
    git(work, "checkout", "-q", c)
    try:
        rc_c, own_c, _ = run_key_script(script, work, tmp, VT_FIDELITY_REQUIRED="false")
        ck("ca key: C's and T's keys differ (T changed an apply input)",
           rc == 0 and rc_c == 0 and own_c.get("applied-name") and own_c.get("applied-name") != own_t.get("applied-name"),
           repr((own_c, own_t)))
        rc, o, log = run_key_script(script, work, tmp, VT_FIDELITY_REQUIRED="false", VT_SUPERSEDED="true", VT_TIP_SHA=t)
        ck("ca key: no fidelity needed -> tip-name is T's own -applied key",
           rc == 0 and o.get("tip-name") == own_t.get("applied-name") and o.get("names") == own_c.get("applied-name"),
           repr((rc, o, own_t, log[-400:])))
        for req in ("true", ""):
            rc, o, log = run_key_script(script, work, tmp, VT_FIDELITY_REQUIRED=req, VT_SUPERSEDED="true", VT_TIP_SHA=t)
            ck("ca key: fidelity required (%r) -> tip-name is T's own -fidelity key" % req,
               rc == 0 and o.get("tip-name") == own_t.get("fidelity-name") and o.get("tip-name", "").endswith("-fidelity"),
               repr((rc, o, own_t, log[-400:])))
        rc, o, _ = run_key_script(script, work, tmp, VT_FIDELITY_REQUIRED="false", VT_SUPERSEDED="false", VT_TIP_SHA=t)
        ck("ca key: not superseded -> no tip-name", rc == 0 and "tip-name" not in o and o.get("names"), repr(o))
        rc, o, _ = run_key_script(script, work, tmp, VT_FIDELITY_REQUIRED="false", VT_SUPERSEDED="true", VT_TIP_SHA="e" * 40)
        ck("ca key: unreadable tip sha -> no tip-name, own key still computed, exit 0",
           rc == 0 and "tip-name" not in o and o.get("names") == own_c.get("applied-name"), repr((rc, o)))
        for blocked in ("vt-tip-apply-inputs.txt", "vt-tip-fidelity-inputs.txt", "ro:vt-tip-apply-inputs.txt"):
            rc, o, log = run_key_script(script, work, tmp, block=blocked, VT_FIDELITY_REQUIRED="false",
                                        VT_SUPERSEDED="true", VT_TIP_SHA=t)
            ck("ca key: tip key write fails (%s) -> fails closed: exit 0, no tip-name, own key computed" % blocked,
               rc == 0 and "tip-name" not in o and o.get("names") == own_c.get("applied-name"),
               repr((rc, o, log[-400:])))
    finally:
        git(work, "checkout", "-q", "master")


CA_KEY_MUTANTS = [
    # The parsed run block is dedented by YAML (10 columns), so these anchors are too.
    ("tip key always uses the -applied key", '    echo "verified-content-apply-$f-fidelity"',
     '    echo "verified-content-apply-$a-applied"'),
    ("tip key reads HEAD instead of the tip", 'key_files "$VT_TIP_SHA" "$RUNNER_TEMP/vt-tip"', 'key_files HEAD "$RUNNER_TEMP/vt-tip"'),
    ("tip key applied/fidelity choice inverted", '  if [ "${VT_FIDELITY_REQUIRED:-}" = "false" ]; then\n    echo "verified-content-apply-$a-applied"',
     '  if [ "${VT_FIDELITY_REQUIRED:-}" != "false" ]; then\n    echo "verified-content-apply-$a-applied"'),
    ("a tip-key failure fails the job instead of the tip lookup",
     '    echo "::warning::could not compute the key of superseding tip $VT_TIP_SHA; no tip lookup (full run unless the own key verifies)."\n',
     '    exit 1\n'),
    ("a key_files write failure is ignored (tip key from a partial file)",
     '  } > "$2-apply-inputs.txt" || return 1\n', '  } > "$2-apply-inputs.txt" || true\n'),
]


def ca_tip_key_tests(tmp):
    try:
        script = ca_key_script()
    except Exception as e:
        check("ca key: the vt-key step script is readable", False, repr(e))
        return
    work = os.path.join(tmp, "ca-key")
    git(tmp, "init", "-q", "-b", "master", work)
    for rel in ("Content/a.sql", "tools/apply-content.sh", "Database/Base/World.sql", "Source/ACE.Database/X.cs",
                ".github/workflows/content-apply-ci.yml"):
        os.makedirs(os.path.dirname(os.path.join(work, rel)), exist_ok=True)
        with open(os.path.join(work, rel), "w") as f:
            f.write(rel + "\n")
    git(work, "add", "-A")
    git(work, "commit", "-q", "-m", "C")
    c = git(work, "rev-parse", "HEAD")
    with open(os.path.join(work, "Content", "b.sql"), "w") as f:
        f.write("b\n")
    git(work, "add", "-A")
    git(work, "commit", "-q", "-m", "T")
    t = git(work, "rev-parse", "HEAD")
    ca_tip_key_cases(script, work, tmp, c, t, check)
    for label, old, new in CA_KEY_MUTANTS:
        if script.count(old) != 1:
            check("ca key mutant applies exactly once: %s" % label, False, "occurrences=%d" % script.count(old))
            continue
        fails = []
        ca_tip_key_cases(script.replace(old, new, 1), work, tmp, c, t,
                         lambda lbl, cond, detail="": fails.append(lbl) if not cond else None)
        check("ca key mutant is killed: %s" % label, bool(fails), "no case failed")


def ca_chain_tests(tmp):
    """combine (pending basis, content-apply label) -> content-apply-ci's covered step -> both gates refuse."""
    out = os.path.join(tmp, "ca-combine.out")
    summ = os.path.join(tmp, "ca-summary")
    env = dict(os.environ, **base_env(**PENDING_COVERED))
    env.update(GITHUB_OUTPUT=out, GITHUB_STEP_SUMMARY=summ, GITHUB_SHA="a" * 40, VT_JOB_LABEL="content-apply",
               VT_TIP_PENDING_RUN_URL="https://x/runs/4242")
    p = subprocess.run([sys.executable, os.path.join(HERE, "superseded.py"), "combine"], env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    lines = open(out).read().splitlines() if os.path.exists(out) else []
    outs = dict(l.split("=", 1) for l in lines if "=" in l)
    summary = open(summ).read() if os.path.exists(summ) else ""
    check("ca chain: combine covers on the pending basis and labels the summary content-apply",
          p.returncode == 0 and outs.get("verified") == "true" and outs.get("source") == "superseded"
          and outs.get("basis") == "pending" and "### content-apply covered by a newer master tip" in summary,
          repr((lines, summary)))
    check("ca chain: the content-apply summary claims no audit, and says the deploy gates refuse this sha",
          "audit range" not in summary and "no audit covers this workflow; the deploy gates refuse this sha" in summary,
          summary)
    c = superseded.combine(base_env(**dict(PENDING_COVERED, VT_TIP_PENDING="")))
    check("ca chain: a push that needs fidelity (pending step skipped, tip-pending empty) -> full run",
          c["verified"] == "false", repr(c))
    try:
        import yaml  # noqa: F401
    except ImportError:
        check("ca chain: PyYAML is importable", False)
        return
    marker = [x for x in ca_steps(load_yaml(CA_WF)) if str(x.get("name", "")).startswith(COVERED_PREFIX)]
    cond = marker[0].get("if") if len(marker) == 1 else None
    m = re.match(r"^steps\.verified\.outputs\.source == '([a-z]+)'$", cond or "")
    check("ca chain: content-apply-ci's covered step runs on this verdict (source %r)" % outs.get("source"),
          bool(m) and m.group(1) == outs.get("source"), repr(cond))
    steps = gate_scripts(CA_GATE_WORKFLOWS, CA_GATE_STEP)
    if shutil.which("jq") is None or not steps:
        print("SKIP  ca chain: no content-apply gate workflow or no jq in this tree")
        return
    job = job_fixture(501, name="content-apply", steps=[
        ("Set up job", "success"), ("Checkout", "success"),
        ("Verified-tree pending check of the superseding tip (master push only)", "success"),
        ("Verified-tree verdict (master push only)", "success"),
        (COVERED_PREFIX + " " + outs.get("covering-sha", ""), "success"),
        ("Start MySQL 8.0 (preinstalled) and set root credentials", "skipped"),
        ("Apply the manifest (first pass)", "skipped"), ("Summary", "success")])
    for wf, st in steps.items():
        rc, gout = run_gate(st["run"], tmp, {"check-runs.json": check_runs_fixture(
            (501, "2026-11-18T10:00:00Z"), name="content-apply", other="build-test"), "job-501.json": job})
        check("ca chain %s: a sha whose content-apply was covered while its tip was pending is refused" % wf,
              rc == 1 and ("was covered by newer tip " + TIP) in gout, repr((rc, gout)))


# ---- lookup.py against a fake REST API ----------------------------------------------------------

REPO = "o/r"
WF = ".github/workflows/build-test.yml"
CA_WF = ".github/workflows/content-apply-ci.yml"


def run_obj(run_id, event="pull_request", conclusion="success", status="completed", branch="train/x",
            path=WF, head_repo=REPO):
    return {"id": run_id, "status": status, "conclusion": conclusion, "event": event,
            "head_branch": branch, "path": path, "html_url": "https://x/runs/%d" % run_id,
            "repository": {"full_name": REPO}, "head_repository": {"full_name": head_repo}}


class FakeApi:
    def __init__(self):
        self.artifacts = {}  # name -> list of run objects
        self.fail = False

    def handler(self):
        api = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                if api.fail:
                    self.send_response(500)
                    self.end_headers()
                    return
                from urllib.parse import urlparse, parse_qs
                u = urlparse(self.path)
                if u.path == "/repos/%s/actions/artifacts" % REPO:
                    name = parse_qs(u.query).get("name", [""])[0]
                    runs = api.artifacts.get(name, [])
                    body = {"total_count": len(runs), "artifacts": [
                        {"id": i + 1, "name": name, "expired": False, "workflow_run": {"id": r["id"]}}
                        for i, r in enumerate(runs)]}
                elif u.path.startswith("/repos/%s/actions/runs/" % REPO):
                    rid = int(u.path.rsplit("/", 1)[1])
                    body = None
                    for runs in api.artifacts.values():
                        for r in runs:
                            if r["id"] == rid:
                                body = r
                    if body is None:
                        self.send_response(404)
                        self.end_headers()
                        return
                else:
                    self.send_response(404)
                    self.end_headers()
                    return
                data = json.dumps(body).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
        return H


def lookup(api_url, names, tmp):
    out = os.path.join(tmp, "lookup.out")
    if os.path.exists(out):
        os.remove(out)
    env = dict(os.environ, GITHUB_TOKEN="t", GITHUB_REPOSITORY=REPO, GITHUB_API_URL=api_url,
               GITHUB_RUN_ID="999", GITHUB_OUTPUT=out, VT_NAMES=names, VT_WORKFLOW_PATH=WF)
    p = subprocess.run([sys.executable, os.path.join(HERE, "lookup.py")], env=env,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    res = {}
    for line in open(out).read().splitlines():
        k, _, v = line.partition("=")
        res[k] = v
    return p.returncode, res


def lookup_tests():
    tmp = tempfile.mkdtemp(prefix="vt-lookup-")
    api = FakeApi()
    srv = http.server.HTTPServer(("127.0.0.1", 0), api.handler())
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    url = "http://127.0.0.1:%d" % srv.server_address[1]
    final_tree_name = "verified-build-test-" + "f" * 40
    member_pr_tree_name = "verified-build-test-" + "e" * 40
    try:
        # The train PR recorded the FINAL tree; a member PR recorded its own (older-base) tree.
        api.artifacts[final_tree_name] = [run_obj(101)]
        api.artifacts[member_pr_tree_name] = [run_obj(102, branch="feature/member")]

        rc, r = lookup(url, final_tree_name, tmp)
        check("lookup: tip tree recorded by the train PR run -> verified",
              rc == 0 and r.get("verified") == "true" and r.get("run-url") == "https://x/runs/101", repr(r))

        rc, r = lookup(url, "verified-build-test-" + "d" * 40, tmp)
        check("lookup: a tip tree no run recorded (PR head was NOT the final tree) -> not verified",
              rc == 0 and r.get("verified") == "false", repr(r))

        api.artifacts[final_tree_name].append(run_obj(103, conclusion="failure"))
        rc, r = lookup(url, final_tree_name, tmp)
        check("lookup: an ambiguous producer (one failed run) -> not verified",
              rc == 0 and r.get("verified") == "false", repr(r))
        api.artifacts[final_tree_name] = [run_obj(104, status="in_progress", conclusion=None)]
        rc, r = lookup(url, final_tree_name, tmp)
        check("lookup: tip's producing run still running -> not verified",
              rc == 0 and r.get("verified") == "false", repr(r))
        api.artifacts[final_tree_name] = [run_obj(105, event="push", branch="master")]
        rc, r = lookup(url, final_tree_name, tmp)
        check("lookup: tip recorded by a successful master push run -> verified",
              rc == 0 and r.get("verified") == "true", repr(r))
        api.artifacts[final_tree_name] = [run_obj(106, head_repo="fork/r")]
        rc, r = lookup(url, final_tree_name, tmp)
        check("lookup: fork producer -> not verified", rc == 0 and r.get("verified") == "false", repr(r))

        api.fail = True
        rc, r = lookup(url, member_pr_tree_name, tmp)
        check("lookup: API error (HTTP 500) -> not verified, exit 0",
              rc == 0 and r.get("verified") == "false", repr(r))
    finally:
        srv.shutdown()
        srv.server_close()

    try:
        # Nothing listens on this port once the server above is closed.
        rc, r = lookup(url, final_tree_name, tmp)
        check("lookup: API unreachable -> not verified, exit 0",
              rc == 0 and r.get("verified") == "false", repr(r))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    tmp = tempfile.mkdtemp(prefix="vt-superseded-")
    try:
        detect_tests(tmp)
        gate_tests(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    combine_tests()
    pending_cases(superseded, check)
    pending_mutation_tests()
    pending_cli_tests()
    ca_wiring_tests()
    tmp = tempfile.mkdtemp(prefix="vt-content-apply-")
    try:
        ca_gate_tests(tmp)
        ca_tip_key_tests(tmp)
        ca_chain_tests(tmp)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    lookup_tests()
    print()
    print("%d passed, %d failed" % (PASSES[0], len(FAILS)))
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
