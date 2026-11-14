# Azure deployment

The GitHub Actions workflows publish and deploy the API image to Azure Container
Apps. PostgreSQL Flexible Server is private to the environment VNet. Azure
Container Registry (ACR) is shared by dev and prod so a prod rollout uses the
same ACR digest built and tested by CI; it does not rebuild or retag an image.
Deployment is manual. The `prod` GitHub environment must be protected with
required reviewers before relying on it.

The deployment templates create/update the environment's VNet, delegated
PostgreSQL and Container Apps subnets, private PostgreSQL and Key Vault DNS
zones/endpoints, PostgreSQL Flexible Server and `booking` database, Container
Apps environment, Log Analytics workspace, private Key Vault, managed identity,
Key Vault secrets, and a manual migration job. They do not create resource
groups, a shared registry, or an external message broker. PostgreSQL and Key
Vault have no public network access. A first deployment can take several
minutes while Azure creates the database and Container Apps environment.

## One-time Azure setup

1. Choose an Azure subscription and location. Create a shared registry resource
   group and one resource group per environment. Deploy the registry template
   once, with a globally unique ACR name:

   ```sh
   az group create --name <registry-resource-group> --location <location>
   az group create --name <dev-resource-group> --location <location>
   az group create --name <prod-resource-group> --location <location>
   az deployment group create --resource-group <registry-resource-group> \
     --template-file infra/registry.bicep \
     --parameters registryName=<globally-unique-acr-name>
   ```

2. Create three Entra applications/service principals for GitHub OIDC (no
   client secrets), separating image publication from dev and prod deployment.
   Add federated credentials with issuer
   `https://token.actions.githubusercontent.com` and audience
   `api://AzureADTokenExchange`:

   * CI identity: `repo:<owner>/<repo>:ref:refs/heads/main`.
   * Dev identity: `repo:<owner>/<repo>:environment:dev`.
   * Prod identity: `repo:<owner>/<repo>:environment:prod`.

   Configure role assignments narrowly. The CI identity needs only `AcrPush`
   on the shared registry. The dev and prod identities must be distinct. Each
   deployment identity needs `Contributor` and `Key Vault Secrets Officer` on
   its own environment resource group and permission to create role
   assignments:
   `User Access Administrator` (or `Role Based Access Control Administrator`)
   at its environment resource group and at the shared registry scope for its
   managed identity's `AcrPull` assignment. Each deployment identity also needs
   `Reader` at the exact shared ACR resource scope: `az acr show` and Bicep's
   existing-registry references read the registry's login server. Scoped
   role-assignment permissions alone do not grant these resource reads.
   Additionally, `platform.bicep` deploys `registry-pull-role.bicep` as an ARM
   nested deployment in the shared registry **resource group**. Both dev and
   prod deployment identities therefore need `Microsoft.Resources/deployments/*`
   at that resource group scope. Exact-ACR `Reader` and role-assignment
   permissions do not grant `Microsoft.Resources/deployments/write` on the
   parent resource group. Use a minimal custom deployment role below; do not
   grant shared-resource-group `Contributor` merely to enable this module.
   `Contributor` alone cannot create role assignments. Prefer custom roles
   with only the required deployment, secret-write, and role-assignment actions
   if your organization supports it.
   The Bicep templates grant the app identity `AcrPull` on the registry and
   `Key Vault Secrets User` on its environment vault.

   An authorized administrator can grant the shared-registry read role to each
   deployment identity (these are setup instructions, not executed by CI):

   ```sh
   az role assignment create --assignee-object-id <deployment-service-principal-object-id> \
     --assignee-principal-type ServicePrincipal --role Reader \
     --scope /subscriptions/<subscription-id>/resourceGroups/<registry-resource-group>/providers/Microsoft.ContainerRegistry/registries/<acr-name>
   ```

   An authorized administrator can create this custom role once, then assign
   it separately to **each** dev/prod deployment identity. Replace placeholders
   before running. This supplements, rather than replaces, the exact-ACR
   `Reader` and scoped role-assignment permissions above:

   ```sh
   az role definition create --role-definition '{
     "Name": "Recoletas Shared Registry Deployment Operator",
     "IsCustom": true,
     "Description": "Manage ARM nested deployments in the shared registry resource group.",
     "Actions": ["Microsoft.Resources/deployments/*"],
     "NotActions": [],
     "DataActions": [],
     "NotDataActions": [],
     "AssignableScopes": ["/subscriptions/<subscription-id>/resourceGroups/<registry-resource-group>"]
   }'
   az role assignment create --assignee-object-id <deployment-service-principal-object-id> \
     --assignee-principal-type ServicePrincipal \
     --role "Recoletas Shared Registry Deployment Operator" \
     --scope /subscriptions/<subscription-id>/resourceGroups/<registry-resource-group>
   ```

3. In repository settings, add Actions **repository variables** `ACR_NAME`,
   `AZURE_CI_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID`. Add
   the environment-specific `AZURE_CLIENT_ID` (dev/prod identity) plus the
   same `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` variables and these
   **environment variables** to both GitHub environments (`dev`, `prod`):

   * `AZURE_LOCATION`, `RESOURCE_GROUP`, `ACR_RESOURCE_GROUP`, `KEY_VAULT_NAME`,
     `VNET_ADDRESS_PREFIX`, and `RABBITMQ_HOSTNAME`.
   * `VNET_ADDRESS_PREFIX` must be a non-overlapping `/16`; for example, use
     `10.40.0.0/16` for dev and `10.50.0.0/16` for prod. The templates reserve
     a `/24` PostgreSQL subnet, a `/23` Container Apps subnet, and a `/27`
     private-endpoint subnet.
   * `KEY_VAULT_NAME` must be globally unique and satisfy Azure Key Vault naming
     rules.

   Add these **environment secrets** to each environment: `POSTGRES_ADMIN_PASSWORD`,
   `JWT_SECRET`, `JWT_ISSUER`, `JWT_AUDIENCE`, `RABBITMQ_VHOST`,
   `RABBITMQ_USERNAME`, and `RABBITMQ_PASSWORD`. Use a unique PostgreSQL
   administrator password per environment; it must be 16-128 characters from
   letters, digits, or `!#%+_-` (the deployment uses it inside a connection
   string). `JWT_SECRET` must contain at least 32 characters. Issuer/audience
   values must match the identity token configuration used by API clients.
   Optionally set `RABBITMQ_TLS_SERVER_NAME` as an environment variable when
   the broker's trusted TLS certificate name differs from its hostname.

4. Provide a separately provisioned, durable RabbitMQ/AMQP broker. It must
   have a publicly trusted TLS certificate and accept AMQPS from Azure
   Container Apps on port 5671. Provide the DNS hostname, a dedicated vhost,
   and credentials; the app always enables TLS in Azure. The deployment does
   not create RabbitMQ, open firewall rules on the broker, or allow an
   ephemeral broker. Confirm the provider's network policy permits outbound
   connections from the Container Apps environment.

5. Protect the GitHub `prod` environment with required reviewers and restrict
   both `dev` and `prod` environment deployments to the `main` branch. Add any
   further secret protections required by your release policy. Environment
   protection is configured in GitHub, not in this repository. Keep dev and
   prod credentials, resource groups, vaults, and VNet CIDRs separate.

## CI and release flow

`ci.yml` runs for pull requests, pushes to any branch, and manual dispatch. It
restores/builds Release, runs `Unit`, PostgreSQL-backed `Integration` and `E2E`
tests, and the explicitly enabled `BrokerIntegration` test against an isolated
vhost/user on an ephemeral RabbitMQ 4.1 service. It builds the Docker image and
compiles the Bicep templates. Only a successful push CI run on `main` uses OIDC
to push an image and publish its sha256 digest as an artifact; other branch
pushes validate without Azure credentials.

Manually run `Deploy Azure Container Apps` and supply `dev` or `prod` plus the
full commit SHA. The workflow accepts only a successful `main` push CI run for
that exact SHA and downloads that run's digest. It provisions the platform,
starts the migration job with that image, captures its returned execution
name, and polls only that execution for up to 20 minutes. Missing/ambiguous
names fail immediately; an older successful execution can never satisfy the
gate. It deploys the API only after that migration succeeds, then
polls `/health/ready` for up to 10 minutes. A failed migration or readiness
check fails the workflow; it does not route the new API revision before a
successful migration. Do not start a second deployment to an environment
while its workflow is running.

## Rollback

Use the digest artifact from the exact earlier successful CI run and manually
dispatch the same deployment workflow for that commit/environment. This
restores the previous application image; it does **not** roll back database
migrations. Review migration compatibility and perform any required database
recovery separately. Never automatically downgrade the schema or reset the
resource group. Digest artifacts are retained for 90 days by default; preserve
the known-good digest and corresponding successful CI run according to your
release-retention policy.

The templates intentionally do not set a prod image to a mutable `latest` tag,
delete resources, or generate a new resource name on each run. Application
container logs and migration job diagnostics are available through the
environment's Container Apps/Log Analytics resources. No connection string,
database password, JWT signing key, or RabbitMQ password is written as a
workflow output.
