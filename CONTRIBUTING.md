# Contributing to MillWorks.AuditCore

Thank you for your interest in contributing to MillWorks.AuditCore. This guide will help you get started.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A C# IDE (Visual Studio, Rider, or VS Code with C# Dev Kit)
- Git

## Getting Started

1. Fork the repository and clone your fork:
   ```bash
   git clone https://github.com/<your-username>/millworks.auditcore.git
   cd millworks.auditcore
   ```

2. Build the solution:
   ```bash
   dotnet build
   ```

3. Run the tests:
   ```bash
   dotnet test
   ```

## Branch Naming Convention

Use the following prefixes for your branches:

- `feature/` -- new functionality (e.g., `feature/add-nist-compliance`)
- `fix/` -- bug fixes (e.g., `fix/hash-chain-validation`)
- `docs/` -- documentation changes (e.g., `docs/update-api-reference`)

## Commit Messages

- Use the imperative mood ("Add feature" not "Added feature")
- Keep the first line under 72 characters
- Reference related issues where applicable (e.g., "Fix hash validation error. Closes #42")

## Pull Request Guidelines

1. Create your branch from `main`.
2. Ensure all existing tests pass (`dotnet test`).
3. Add tests for any new functionality.
4. Follow the existing code style and conventions in the repository.
5. Do not introduce breaking changes without prior discussion in an issue.
6. Keep pull requests focused -- one logical change per PR.

## Code Style

- Follow the conventions already established in the codebase.
- Use the `.editorconfig` if one is present.
- Prefer clarity over cleverness.

## Reporting Issues

Please use [GitHub Issues](https://github.com/jesserules/millworks.auditcore/issues) to report bugs or request features. For security vulnerabilities, see [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
