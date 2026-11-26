# Changelog

## 0.1.0 (2026-11-26)
- First version: messages (send, send to many, status, list, scheduled, cancel, reschedule), inbox, devices, one-time passwords, account, balance and statistics.
- .NET 8, 9 and 10. No dependencies. Async with `CancellationToken`, `await foreach` for lists.
- Typed results and typed exceptions (`AuthenticationException`, `PermissionDeniedException`, `InvalidRequestException`, `NotFoundException`, `RateLimitException`, `ServerException`, `ConnectionException`).
- Safe retries with the same `Idempotency-Key`, `Retry-After` support, 30 second timeout.
- `Webhook.Verify` with typed events, `Webhook.Sign`, `QMGateway.Testing.FakeTransport` and the `QMGateway.Cli` command-line tool.
- Security: a caller-supplied idempotency key must be 1 to 255 visible characters with no spaces, so it cannot be used to inject a header.
