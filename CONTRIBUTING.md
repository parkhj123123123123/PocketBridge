# Contributing

Open an issue before a large protocol or architecture change. Keep transfer protocol changes backward-compatible or increment the protocol version in both native apps and the relay documentation.

Before a pull request, run:

```powershell
dotnet build PocketBridge.slnx -c Release
dotnet run --project tests/PocketBridge.Tests -c Release
dotnet run --project tests/RelaySmoke -c Release
```

iOS changes should also build on the current stable Xcode with the deployment target declared in `ios/project.yml`. Never commit signing assets, provisioning profiles, production relay credentials, QR contents, or captured user files.
