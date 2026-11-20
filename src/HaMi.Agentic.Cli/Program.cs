using HaMi.Agentic.Cli.Features.Replay;
using HaMi.Agentic.Cli.Features.Workflow;

return args.Length > 0 && string.Equals(args[0], "workflow", StringComparison.Ordinal)
    ? await WorkflowCommand.RunAsync(args[1..])
    : await ReplayCommand.RunAsync(args);
