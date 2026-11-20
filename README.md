# Greyhound Trader

Source for the greyhound web app, monitor, distance tests and operational startup scripts.

Requires Windows, .NET 8 SDK, Chrome, external credential files, MariaDB, RabbitMQ and pricing browser bridges. The shared Betfred OFF browser service is maintained in [racing](https://github.com/jarlontrading/racing); record its deployed version separately.

The initial source import is from the working Dropbox tree on 20 November 2026. It may include changes not yet deployed. Fresh checkout defaults disable hosted workers and select DEMO publishing; use isolated credentials and databases before running anything. Startup code can still access configured databases.

Use feature branches and pull requests. CI runs the distance test executable and builds a versioned package; a version tag creates a draft GitHub release. Deployment and rollback are separate manual operations. See [the release process](docs/RELEASING.md).

Credentials, databases, browser profiles, captured responses and build outputs are excluded. Existing startup scripts still reference this machine's legacy locations. External Betfred pricing bridge binaries are dependencies; their source was not present in the imported web project.
