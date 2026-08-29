# Deployment Guide

MessageBridge production platform uses OpenTofu for Azure Container Apps and
GitHub Actions `delivery.yml` for application delivery. Do not use local Docker
publication, direct Container Apps updates, or direct migration-job starts for
an environment release.

The worker image repository is `ghcr.io/chanakya-net/whatsapp-messaging/worker`.
Delivery publishes and deploys it only by immutable digest. The image supports
`linux/amd64` and `linux/arm64`, runs as a non-root user, and serves liveness at
`/health/live` and readiness at `/health/ready`.

## Safe configuration model

- OpenTofu owns Container Apps, PostgreSQL, Key Vault, identities, and alerting.
- Runtime secrets use versionless Key Vault references. Values are entered only
  through the Key Vault portal and are never retrieved, printed, or passed to a
  command.
- The only approved secret names are `rabbitmq-connection-string`,
  `new-relic-otlp-headers`, `whatsapp-provider-placeholder`, and
  `email-provider-placeholder`.
- The repository supports only `centralindia` and its `cin` name token. Names
  and resource groups come from OpenTofu output; never guess a serial or name.

For a local worker test, use the repository's non-secret configuration template
and a local test broker/database provisioned outside this guide. Do not place
credentials in command arguments, environment variables, source files, or logs.

## Foundation and application lifecycle

The operator procedure, including all approvals and recovery boundaries, is in
[Bootstrap and Deployment Runbook](./runbooks/deployment.md). The lifecycle is:

1. Bootstrap state and OIDC with `.tofu/bootstrap`.
2. Apply reviewed metadata-only plans for `.tofu/envs/shared`, then `dev`, then
   `prod`.
3. Create placeholder Key Vault entries, replace them through the portal after
   the external systems are ready, and validate versionless references.
4. Run the protected delivery workflow. It publishes verified immutable images,
   migrates dev, releases dev, validates its handoff, and only then awaits the
   protected production approval.

## Manual delivery operations

`delivery.yml` is the sole ordered application-delivery path. Set
`DELIVERY_REF` to a reviewed commit SHA or the protected delivery branch; do
not use an unreviewed ref. The workflow accepts no digest, tag, credential, or
secret-value input.

```bash
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=publish -f environment=none
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=dev -f environment=none
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=prod -f environment=none
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=reload-secrets -f environment=dev
gh workflow run delivery.yml --ref "$DELIVERY_REF" -f target=reload-secrets -f environment=prod
```

`publish` validates and anonymously verifies public GHCR artifacts. `dev`
updates the migration image to the reviewed immutable digest, waits for the
migration, changes the worker only after migration success, smoke-tests it, and
rolls the worker back if later release checks fail. `prod` requires that exact
successful dev handoff, then GitHub Environment approval and protected
non-cancelling concurrency before production OIDC is issued. Neither release
nor rollback reverses a database schema.

Configure **required reviewers** for the `prod` GitHub Environment. Secret
values are updated in Key Vault **out of band**. Operators never pass a secret to a workflow. A release reload rollback restores the verified **prior revision**
before reporting recovery, and delivery never reverses the database schema.
Production approval occurs before production OIDC, and delivery never reverses database schema automatically.

### Mutating action contract

Use this contract for every delivery, portal, or OpenTofu mutation.

- Target: the environment/resource resolved from its OpenTofu output.
- Inputs: reviewed non-secret metadata and the selected workflow target/ref.
- Safe path: the commands above or the linked runbook portal path.
- Expected result: GitHub run summary reports the protected operation succeeded.
- Failure interpretation: no successful completion means stop; inspect the
  sanitized workflow summary and follow the linked recovery runbook.
- Approval boundary: `prod` requires protected GitHub Environment approval;
  foundation and portal changes require recorded platform-owner approval.
- Cleanup: retain the run URL and approval record; use the linked runbook for
  temporary-resource cleanup or recovery.

## Operational links

- [Deployment runbook](./runbooks/deployment.md)
- [Rollback](./runbooks/rollback.md)
- [Migration failure](./runbooks/migration-failure.md)
- [Secret rotation](./runbooks/secret-rotation.md)
- [CloudAMQP outage](./runbooks/cloudamqp-outage.md)
- [Quarterly database restore drill](./runbooks/database-restore.md)
