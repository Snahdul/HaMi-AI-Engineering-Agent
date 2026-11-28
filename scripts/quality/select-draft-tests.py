"""Conservative incremental Draft Linux test selection; never a merge gate."""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

FULL = "full"
ORDER = "order-recovery"
DEV = "src/HaMi.Agentic.Cli/Features/Development/"
# Exact reviewed paths only. Shared stores, contracts for other slices,
# project files, dependencies, CI, unknown/new files => full suite.
ORDER_PATHS = frozenset({
    DEV + "DevelopmentOrderOperationLedger.cs",
    DEV + "DevelopmentOrderOperationLedgerTests.cs",
    DEV + "DevelopmentOrderCandidateCheckpoint.cs",
    DEV + "DevelopmentOrderBudgetPolicy.cs",
    "policies/development/pair-budget.fixture.policy.json",
    "tests/HaMi.Agentic.JobState.Probe/PersistenceBridgeProbe.cs",
    "docs/decisions/0006-persistent-development-order-journal.md",
    "docs/developer-order-contract.md",
})


def select_paths(changed):
    return ORDER if changed and set(changed) <= ORDER_PATHS else FULL


def sha(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{40}", value) and value != "0" * 40


def event_range(event_name, event):
    if event_name != "pull_request" or event.get("action") != "synchronize":
        return None
    pr = event["pull_request"]
    if pr["draft"] is not True:
        return None
    if any(label["name"] == "ci:full" for label in pr["labels"]):
        return None
    before, after, base = event.get("before"), event.get("after"), pr["base"]["sha"]
    if not all(sha(value) for value in (before, after, base, pr["head"]["sha"])):
        return None
    if before == after or after != pr["head"]["sha"]:
        return None
    return before, after, base


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.DEVNULL, timeout=15)


def classify(event_name, event):
    try:
        bounds = event_range(event_name, event)
        if bounds is None:
            return FULL
        before, after, base = bounds
        # The default PR checkout is GitHub's merge commit, not the head.
        # Validate the exact parents; never infer a previous head from HEAD~1.
        if git("rev-list", "--parents", "-n", "1", "HEAD").decode().split()[1:] != [base, after]:
            return FULL
        git("merge-base", "--is-ancestor", before, after)
        if git("merge-base", before, base) != git("merge-base", after, base):
            return FULL
        # Compare both endpoints of the pushed range. Disable rename detection
        # so deletion of an unrelated path cannot disappear into a rename.
        raw = git("diff", "--no-ext-diff", "--no-renames", "--name-only", "-z", before, after, "--")
        if not raw or not raw.endswith(b"\0"):
            return FULL
        return select_paths(raw[:-1].decode("utf-8", errors="strict").split("\0"))
    except (KeyError, TypeError, AttributeError, OSError, ValueError, subprocess.SubprocessError):
        return FULL


def self_test():
    def require(condition):
        if not condition:
            raise AssertionError("Draft test selection regression")

    one = DEV + "DevelopmentOrderOperationLedger.cs"
    two = DEV + "DevelopmentOrderCandidateCheckpoint.cs"
    require(select_paths([one]) == ORDER)
    require(select_paths([one, two]) == ORDER)
    require(select_paths([]) == FULL)
    for other in (
        "Directory.Build.props", "Directory.Packages.props", "global.json",
        ".github/workflows/dotnet.yml", "scripts/quality/select-draft-tests.py",
        DEV + "DevelopmentJobStateStore.cs", DEV + "DevelopmentOrderApproval.cs",
        DEV + "NewFile.cs", "unknown.cs", one + "\n",
    ):
        require(select_paths([one, other]) == FULL)
    import copy
    before, after, base = "a" * 40, "b" * 40, "c" * 40
    good = {"action": "synchronize", "before": before, "after": after,
            "pull_request": {"draft": True, "labels": [],
                             "head": {"sha": after}, "base": {"sha": base}}}
    require(event_range("pull_request", good) == (before, after, base))
    require(event_range("workflow_dispatch", good) is None)
    for action in ("opened", "reopened", "ready_for_review", "converted_to_draft"):
        require(event_range("pull_request", dict(good, action=action)) is None)
    for change in ({"before": None}, {"after": base}, {"before": after},
                   {"before": "0" * 40}, {"before": "--help"}):
        require(event_range("pull_request", dict(good, **change)) is None)
    review = copy.deepcopy(good)
    review["pull_request"]["draft"] = False
    require(event_range("pull_request", review) is None)
    full = copy.deepcopy(good)
    full["pull_request"]["labels"] = [{"name": "ci:full"}]
    require(event_range("pull_request", full) is None)
    require(classify("pull_request", {}) == FULL)
    require(classify("pull_request", {"action": "synchronize"}) == FULL)
    print("PASS: conservative draft test selection (paths, event, full fallback).")


def main():
    if sys.argv[1:] == ["--self-test"]:
        self_test()
        return
    try:
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        profile = classify(os.environ["GITHUB_EVENT_NAME"], event)
    except (KeyError, OSError, ValueError):
        profile = FULL
    print("Draft test profile: " + profile)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write("profile=" + profile + "\n")


if __name__ == "__main__":
    main()
