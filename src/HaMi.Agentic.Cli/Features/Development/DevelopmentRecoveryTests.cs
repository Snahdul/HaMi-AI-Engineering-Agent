namespace HaMi.Agentic.Cli.Features.Development;

internal static class DevelopmentRecoveryTests
{
    internal static async Task SelfTestAsync(string repo)
    {
        string id = new('a', 32);
        string baseline = new('b', 40);
        string name = "hami-code-" + id[..12];
        string missing = "error: sandbox '" + name + "'\nnot found   try: sbx ls";
        if (!DevelopmentSandboxRunner.IsConfirmedAbsent(id, 1, "", missing)
            || !DevelopmentSandboxRunner.IsConfirmedAbsent(id, 1, "error: sandbox '" + name + "'", "not found")
            || DevelopmentSandboxRunner.IsConfirmedAbsent(id, 0, "", missing)
            || DevelopmentSandboxRunner.IsConfirmedAbsent(id, 1, "", "backend not found: " + name)
            || DevelopmentSandboxRunner.IsConfirmedAbsent(id, 1, "", "permission denied for " + name)
            || DevelopmentSandboxRunner.IsConfirmedAbsent(id, 1, "", missing + "\nbackend unavailable")
            || DevelopmentSandboxRunner.IsConfirmedAbsent(new string('c', 32), 1, "", missing))
            throw new InvalidOperationException("Recovery independent absence classification regression.");

        // These are parser-boundary failures; none may enter the recovery
        // command, open a production claim, read a token or contact sbx.
        string[] args = ["--repo", repo, "--agent-resume", id,
            "--publish-draft", "--publication-base", baseline];
        foreach (string[] suffix in new string[][]
        {
            ["--live"], ["--model", "test"], ["--issue", "62"],
            ["--results-dir", "untrusted"], ["--contract", DevelopmentAgentContract.ProductionPath],
            ["--source", "untrusted"], ["--project", "untrusted"], ["--mode", "run"],
            ["--worktree", "untrusted"], ["--publish-issue"], ["--allow-followups"],
            ["--agent-run"], ["--agent-resume", id], ["--publication-base", baseline]
        })
        {
            if (await DevelopmentCommand.RunAsync([.. args, .. suffix]) != 1)
                throw new InvalidOperationException("Recovery CLI accepted an unauthorized override.");
        }
        if (await DevelopmentCommand.RunAsync(["--repo", repo, "--agent-resume", id]) != 1
            || await DevelopmentCommand.RunAsync(["--repo", repo, "--agent-resume", "../bad",
                "--publish-draft", "--publication-base", baseline]) != 1)
            throw new InvalidOperationException("Recovery CLI accepted missing authorization or invalid identity.");
    }
}
