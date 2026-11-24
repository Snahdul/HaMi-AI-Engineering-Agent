# Reproduce a bounded coding retry

[Back to README](../../README.md)

From the repository root with the SDK installed:

```sh
dotnet run --project src/HaMi.Agentic.Cli -c Release -- development --repo . --agent-self-test
```

This offline self-test exercises CLI parsing, the versioned task contract, a compiler-error scenario followed by a successful retry, exhausted retry budget and timeout without another sandbox start. It uses controlled test components, not a live model or real MicroVM.

For a complementary offline test that uses the real .NET compiler and deterministic tests on a restricted expression:

```sh
dotnet run --project src/HaMi.Agentic.Cli -c Release -- coding --self-test --repo .
```

Both commands are present in the [CI workflow](../../.github/workflows/dotnet.yml). The real MicroVM experiment documented in [development history](../development-history.md) uses a scripted candidate with CS0103 followed by a corrected candidate, each in a fresh MicroVM. That experiment has separate environment requirements and is not run by these offline commands.

The purpose is to demonstrate controlled correction and termination, not general autonomous programming.
