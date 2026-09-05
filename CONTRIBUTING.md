# Contributing

Open an issue before a large protocol or architecture change. Keep transfer protocol changes backward-compatible or increment the protocol version in the Windows receiver, relay, Shortcut recipe, and documentation.

Before a pull request, run:

```powershell
dotnet build PocketBridge.slnx -c Release
dotnet run --project tests/PocketBridge.Tests -c Release
dotnet run --project tests/RelaySmoke -c Release
```

Never commit production relay credentials, QR contents, bearer tokens, or captured user files. If you change the Shortcut request headers or recipe, update `docs/shortcut.md` and add an integration test.
