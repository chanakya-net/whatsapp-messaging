# Package Publishing

MessageBridge NuGet packages are published through a dedicated workflow, not through the normal CI pipeline.

## Published packages

- `MessageBridge.Contracts`
- `MessageBridge.Publisher`
- `MessageBridge.Publisher.EntityFrameworkCore`

## Current package metadata

The repo already carries the shared package metadata in `Directory.Build.props`:

- repository URL: `https://github.com/chanakya-net/whatsapp-messaging`
- package project URL: `https://github.com/chanakya-net/whatsapp-messaging`
- package license: `AGPL-3.0-only`
- symbol package format: `snupkg`

`MessageBridge.Publisher` currently declares `Authors=MessageBridge` and `Version=1.0.0` in its project file. The publish workflow overrides package version at pack time so the published output follows the tag or manual version supplied at release time.

## Required GitHub configuration

Set these repository secrets before enabling publishing:

- `NUGET_SOURCE_URL` - private NuGet feed endpoint.
- `NUGET_API_KEY` - feed credential or API key.

If the chosen private feed uses a different authentication scheme, keep the workflow gated and adapt the secret names and push command to match that operator decision. Do not hard-code secrets into the repo.

## Triggers

The workflow in `.github/workflows/publish-packages.yml` supports two publish paths:

- manual dispatch with `publish=true` and a `package_version` input
- tag push matching `v*`, where the workflow strips the leading `v` and uses the tag as the package version

Use tags such as `v1.2.3` for release publishing.

## Release flow

1. Verify the private feed secret values are configured in GitHub.
2. Run the publish workflow in validation-only mode first if you want a dry run.
3. For a real release, either:
   - dispatch the workflow with `publish=true` and `package_version=1.2.3`, or
   - push a signed release tag such as `v1.2.3`
4. The workflow restores the solution, builds in `Release`, runs package-focused tests, packs all MessageBridge NuGet packages, and only then pushes the `.nupkg` files to the configured feed.

## Safety

- Normal CI does not publish packages.
- The publish workflow is isolated from `main` and pull-request builds.
- The publish job fails early if the required feed secrets are missing.
