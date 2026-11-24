# Capabilities and roadmap

[Back to README](../README.md)

| Capability | Status and boundary |
| --- | --- |
| Historical issue replay | Implemented for a curated reference case |
| Ordered document workflow | Implemented for a fixed two-step manifest |
| Coding validation | Implemented for bounded C# tasks with compiler/test feedback |
| Development task contracts | Implemented with scope preflight |
| Actor-driven retry | Implemented for the bounded development slice |
| MicroVM execution | Local experimental runner; real host checks documented, not a general security guarantee |
| Structured logging | Implemented with correlated job and sandbox events |
| Live model generation | Explicit opt-in, bounded experimental use |
| General issue-driven autonomous development | Planned / evolving; not claimed as a completed capability |

The next direction is longer autonomous execution within explicit contracts and independently enforced policies. Human approval remains necessary for consequential decisions and merges. See the [project board](https://github.com/users/Snahdul/projects/2/views/1) and [development history](development-history.md) for evidence and limitations.
