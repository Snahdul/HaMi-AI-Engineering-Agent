using HaMi.Agentic.Cli.Features.Coding;
using HaMi.Agentic.Cli.Features.Development;
using HaMi.Agentic.Cli.Features.Replay;
using HaMi.Agentic.Cli.Features.Workflow;
using HaMi.Agentic.Cli.Runtime;

return args.Length > 0 && string.Equals(args[0], "config", StringComparison.Ordinal)
    ? RuntimeSettings.RunConfigurationCommand(args[1..])
    : args.Length > 0 && string.Equals(args[0], "development", StringComparison.Ordinal)
        ? await DevelopmentCommand.RunAsync(args[1..])
    : args.Length > 0 && string.Equals(args[0], "coding", StringComparison.Ordinal)
        ? await CodingCommand.RunAsync(args[1..])
        : args.Length > 0 && string.Equals(args[0], "workflow", StringComparison.Ordinal)
            ? await WorkflowCommand.RunAsync(args[1..])
            : await ReplayCommand.RunAsync(args);
