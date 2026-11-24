using System.CommandLine;
using HaMi.Agentic.Cli.Features.Coding;
using HaMi.Agentic.Cli.Features.Development;
using HaMi.Agentic.Cli.Features.Replay;
using HaMi.Agentic.Cli.Features.Workflow;

namespace HaMi.Agentic.Cli.Runtime;

/// <summary>
/// System.CommandLine is the single parsing/dispatch boundary for named
/// commands. Existing feature use cases remain intact while migrated.
/// Replay is available only through the explicit 'replay' subcommand.
/// </summary>
internal static class CommandLineRouter
{
    public static Task<int> RunAsync(string[] args)
    {
        RootCommand root = CreateRoot(args);
        return root.Parse(args).InvokeAsync();
    }

    private static RootCommand CreateRoot(string[] args)
    {
        RootCommand root = new("HaMi AI Engineering Agent");

        Command config = new("config", "Inspect runtime configuration (never prints secrets)");
        Add(config, "--repo");
        Add(config, "--check", flag: true);
        Add(config, "--self-test", flag: true);
        config.SetAction((_, _) => Task.FromResult(
            RuntimeSettings.RunConfigurationCommand(args[1..])));
        root.Subcommands.Add(config);

        Command development = new("development",
            "Human-scoped C# development, isolated sandbox and actor-coordinated jobs");
        foreach (string name in new[]
        {
            "--repo", "--results-dir", "--source", "--project", "--mode",
            "--contract", "--worktree", "--model", "--issue"
        }) Add(development, name);
        foreach (string name in new[]
        {
            "--self-test", "--contract-check", "--worktree-check",
            "--sandbox-poc", "--sandbox-poc-self-test", "--sandbox-run",
            "--sandbox-run-self-test", "--agent-run", "--agent-self-test", "--live",
            "--publish-issue", "--allow-followups"
        }) Add(development, name, flag: true);
        development.SetAction((_, _) => DevelopmentCommand.RunAsync(args[1..]));
        root.Subcommands.Add(development);

        Command coding = new("coding", "Legacy bounded expression coding workflow");
        foreach (string name in new[] { "--repo", "--task", "--model" })
            Add(coding, name);
        foreach (string name in new[]
        {
            "--unattended", "--self-test", "--self-test-ai",
            "--self-test-logging", "--policy-check", "--ai-extensions"
        }) Add(coding, name, flag: true);
        coding.SetAction((_, _) => CodingCommand.RunAsync(args[1..]));
        root.Subcommands.Add(coding);

        Command workflow = new("workflow", "Versioned Markdown workflow");
        foreach (string name in new[] { "--repo", "--manifest", "--model" })
            Add(workflow, name);
        foreach (string name in new[] { "--self-test", "--validate", "--unattended" })
            Add(workflow, name, flag: true);
        workflow.SetAction((_, _) => WorkflowCommand.RunAsync(args[1..]));
        root.Subcommands.Add(workflow);

        Command replay = new("replay", "Legacy replay workflow (explicit subcommand)");
        foreach (string name in new[] { "--repo", "--model", "--check" })
            Add(replay, name);
        Add(replay, "--unattended", flag: true);
        replay.SetAction((_, _) => ReplayCommand.RunAsync(args[1..]));
        root.Subcommands.Add(replay);

        return root;
    }

    private static void Add(Command command, string name, bool flag = false)
    {
        if (flag) command.Options.Add(new Option<bool>(name));
        else command.Options.Add(new Option<string>(name));
    }

    internal static void SelfTest()
    {
        RootCommand valid = CreateRoot(["development", "--repo", ".", "--agent-self-test"]);
        if (valid.Parse(["development", "--repo", ".", "--agent-self-test"]).Errors.Count != 0
            || valid.Parse(["development", "--repo", ".", "--sandbox-run", "--source", "snapshot",
                "--project", "Demo.csproj", "--mode", "run", "--results-dir", "reports"]).Errors.Count != 0
            || valid.Parse(["coding", "--repo", ".", "--unattended", "--task", "clamp-kata"]).Errors.Count != 0
            || valid.Parse(["workflow", "--repo", ".", "--manifest", "workflows/mvp02.json", "--validate"]).Errors.Count != 0
            || valid.Parse(["development", "--repo", ".", "--model"]).Errors.Count == 0
            || valid.Parse(["development", "--repo", ".", "--agent-run", "--results-dir", "C:/reports",
                "--issue", "42", "--publish-issue", "--allow-followups"]).Errors.Count != 0
            || valid.Parse(["development", "--unknown-option"]).Errors.Count == 0
            || valid.Parse(["--check", "."]).Errors.Count == 0
            || valid.Parse(["--unattended"]).Errors.Count == 0)
            throw new InvalidOperationException("CLI parser accepted implicit legacy Replay dispatch or rejected a named command.");
    }
}
