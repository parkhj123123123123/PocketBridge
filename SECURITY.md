# Security Policy

PocketBridge handles private files and cryptographic pairing data. Do not post a QR code, pairing JSON, bearer token, private key, signing certificate, or unredacted production log in a public issue.

Report suspected vulnerabilities through a private GitHub Security Advisory for this repository. Include the affected commit, platform version, impact, and the smallest safe reproduction. For an active compromise, stop the relay, rotate any infrastructure credentials, and create a new pairing session; pairing credentials are single-use and cannot resume an interrupted session.

Only maintained releases receive security fixes. The initial development version has not completed an independent security audit.
