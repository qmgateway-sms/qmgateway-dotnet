# Contributing to the QMGateway .NET SDK

Thank you for helping. Small, focused changes are easiest to review. Report security problems privately: see [SECURITY.md](SECURITY.md), not a public issue.

## Set up

.NET SDK 10 (it builds for .NET 8, 9 and 10). There are no NuGet packages to restore beyond the SDK itself.

## Before you open a pull request

1. Fetch the shared contract next to the tests, then run the tests (the contract is the same for all four SDKs):
   ```
   git clone --depth 1 https://github.com/qmgateway-sms/qmgateway-contract.git tests/contract
   dotnet run --project tests/QMGateway.Tests
   ```
   Also run `dotnet build src/QMGateway -c Release` (it builds for net8.0, net9.0 and net10.0 and warnings are errors). A change that makes the contract fail will not be merged.
2. Add or update a test for every change in behaviour.
3. Keep the public surface small and the code readable. This SDK is meant to be read.
4. Do **not** add a runtime dependency without opening an issue first. The reason must be strong.
5. Do **not** log API keys, webhook secrets, phone numbers or message text.
6. If you have a QMGateway test organisation, also run `dotnet run --project tests/QMGateway.Tests -- live` with `QMGATEWAY_BASE`, `QMGATEWAY_LIVE_KEY` and `QMGATEWAY_TEST_KEY` set. It queues real messages with the live key, so never use a production account.

Open pull requests against `main` of `qmgateway-sms/qmgateway-dotnet`. Changes under `.github/` and to the package manifest need a review from `@qmgateway-sms`.

## Rules every release must meet

- The shared contract passes against the real API and against the mock server.
- The webhook verifier passes all cases in `webhook-cases.json`.
- The retry behaviour passes the cases in `retry-cases.json` (same idempotency key on every try, `Retry-After` honoured and capped at 60 seconds, no retry on other 4xx).
- The README examples run exactly as written.
- The change is described in `CHANGELOG.md`, and the version follows semantic versioning (`1.x` talks to API `v1`).
- It is published by CI from a signed tag. Never from a laptop, and never by moving an existing tag.
