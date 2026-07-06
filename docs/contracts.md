# Contracts

## Contract evolution baseline

The contract package baseline for breaking-change checks is the current `main` branch.

Use these commands from repository root:

- `buf lint`
- `buf breaking --against '.git#branch=main'`
- `dotnet test tests/MessageBridge.Contracts.Tests/MessageBridge.Contracts.Tests.csproj`

If `main` is updated with new contract-safe additive changes, no additional baseline file is required.
