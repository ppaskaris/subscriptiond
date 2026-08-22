# CLI

## Start the Dev Server

Use `scripts/start-dev.ps1` to run the app without Visual Studio and without
locking the normal compiler output under `youtubed/bin` or `youtubed/obj`.

```powershell
.\scripts\start-dev.ps1
```

The script publishes a Debug copy to `artifacts/dev-server/<timestamp>` and runs
that copy in the foreground. Tests can still build and run while the dev server
is up because the running process is not holding files in the project's compiler
output directories.

By default it uses the same URLs as the Visual Studio profile:

- `https://localhost:65503`
- `http://localhost:65504`

Use `Ctrl+C` to stop the server.

## Deploy

Use `scripts/deploy.ps1` to deploy directly to the Azure App Service with Web
Deploy.

In the Azure portal, open the App Service, click **Download publish profile**,
and put the downloaded `.PublishSettings` file under `.local/azure` in this
repository. Files ending in `.PublishSettings` are ignored by git.

```powershell
.\scripts\deploy.ps1
```

The script auto-detects the local publish settings file when exactly one is
available, reads the `publishMethod="MSDeploy"` profile, and uses its Web Deploy
endpoint, app path, user name, and password.

The publish settings file contains the deploy password, so keep it local and do
not paste its contents into commits, issues, logs, or screenshots.
