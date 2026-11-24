# Getting started

[Back to README](../README.md)

Install Git and the .NET SDK specified in [global.json](../global.json). From a terminal:

```sh
git clone https://github.com/Snahdul/HaMi-AI-Engineering-Agent.git
cd HaMi-AI-Engineering-Agent
dotnet build src/HaMi.Agentic.Cli/HaMi.Agentic.Cli.csproj --configuration Release
dotnet run --project src/HaMi.Agentic.Cli -- --help
dotnet run --project src/HaMi.Agentic.Cli -c Release -- development --repo . --agent-self-test
```

The offline self-test needs neither Docker nor an API key. See [the retry example](examples/coding-retry.md).

For live experiments, an OpenAI API credential and explicit opt-in are required. Keep credentials outside Git and logs. The general sandbox runner has additional Docker Sandbox and environment requirements; consult the [historical operating instructions](development-history.md) and [security policy](../SECURITY.md) before attempting live execution. Historical commands are not a current quickstart.
