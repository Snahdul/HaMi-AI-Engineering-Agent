using HaMi.Agentic.Cli.Runtime;

// The CLI requires named subcommands; no implicit historical replay dispatch.
return await CommandLineRouter.RunAsync(args);
