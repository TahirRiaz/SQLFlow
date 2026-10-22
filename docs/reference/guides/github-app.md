---
id: guide-github-app
title: "Reading a private GitHub repository with a GitHub App"
type: guide
summary: Create a read-only GitHub App, install it on a flow repository, and have SQLFlow read with its short-lived tokens instead of a personal access token.
keywords:
  - github app
  - git credential
  - private repository
  - personal access token
  - installation token
  - read-only
  - SQLFLOW_GITHUB_APP_ID
  - SQLFLOW_GITHUB_APP_PRIVATE_KEY
  - githubapp
  - repository source
  - key vault
  - permissions
related:
  - concept-connections-and-secrets
  - concept-control-plane
  - concept-environment-variables
  - guide-deployment
  - flow-scm
  - cli-worker
sourceRefs:
  - src/SqlFlow.SourceControl/GitHub/GitHubAppEnvironment.cs
  - src/SqlFlow.SourceControl/GitHub/GitHubAppReference.cs
  - src/SqlFlow.SourceControl/GitHub/GitHubAppTokenClient.cs
  - src/SqlFlow.SourceControl/GitHub/GitHubAppPrivateKey.cs
  - src/SqlFlow.SourceControl/GitHub/GitHubAppSecretProvider.cs
  - src/SqlFlow.Node/GitMaterializer.cs
  - src/SqlFlow.ControlPlane/Api/RepoSourceEndpoints.cs
  - src/SqlFlow.Execution/SqlFlowEngineServices.cs
  - gui/src/features/repos/GitCredentialsSheet.tsx
---

# Reading a private GitHub repository with a GitHub App

A repository source on a private GitHub repository needs a credential. A personal access token works, but it belongs
to one person: it stops when they leave or their access changes, many organizations refuse or must approve it, and it
reaches more than the one repository. A GitHub App belongs to the organization instead. It is installed on the
repositories you choose, it can do only what its permissions say, and SQLFlow asks it for a token that lasts an hour
each time it needs one, so no token is ever stored.

SQLFlow reads a repository (a sync, a discover, the repository's tree and history, a worker checking out the commit a
run is pinned to) with **read-only** access. Every token it asks for is narrowed to the one repository it reads and to
reading its contents.

## 1. Create the App

1. On GitHub, open the organization's **Settings**, then **Developer settings**, **GitHub Apps**, **New GitHub App**.
   For a repository under a personal account, use your own **Settings**, **Developer settings**, **GitHub Apps**.
2. **GitHub App name**: for example `acme-sqlflow-reader`. **Homepage URL**: the deployment's address, or the
   organization's page.
3. **Webhook**: clear **Active**. SQLFlow needs no webhook.
4. **Permissions**, **Repository permissions**:

   | Permission | Access | Why |
   |---|---|---|
   | Contents | Read-only | Clone, fetch and list the repository's branches. |
   | Metadata | Read-only | GitHub sets it for every App. |
   | Everything else | No access | |

   Leave **Organization permissions** and **Account permissions** at **No access**.
5. **Where can this GitHub App be installed?**: **Only on this account**.
6. Select **Create GitHub App**.
7. On the App's page, copy the **Client ID** (it starts with `Iv`). The numeric **App ID** works too.
8. Under **Private keys**, select **Generate a private key**. GitHub downloads a `.pem` file. Keep it like a
   password: whoever holds it can act as the App.

Read-only is enough for everything except proposals (opening a pull request with changed flow files) and the scm flow's
pushes. Those need **Contents: Read and write**, and proposals also **Pull requests: Read and write**. A read-only App
asked to push is refused with a message naming the permissions it lacks.

## 2. Install it on the repository

1. On the App's page, select **Install App**, then the organization.
2. Choose **Only select repositories**, pick the flow repository, and select **Install**.

SQLFlow finds the installation from the repository's URL, so you need no installation id. To give the App another
repository later, add it under the organization's **Settings**, **GitHub Apps**, the App, **Configure**.

## 3. Give SQLFlow the App

There are two ways. Use one.

### The host's App, in its environment

Set these on the control plane and on every worker (each mints its own tokens):

| Variable | Value |
|---|---|
| `SQLFLOW_GITHUB_APP_ID` | The App's client ID (or App ID). |
| `SQLFLOW_GITHUB_APP_PRIVATE_KEY` | The private key: the `.pem` file's text (line breaks or `\n` escapes), the base64 of the file, or a reference to either such as `${keyvault:my-vault/github-app-private-key}`. |
| `SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH` | Instead of the variable above: the absolute path of the `.pem` file (a mounted secret, or the downloaded file on a developer's machine). |
| `SQLFLOW_GITHUB_APP_API_URL` | Only for GitHub Enterprise Server (`https://<host>/api/v3`) or GHE.com (`https://api.<name>.ghe.com`). |

A repository source that names no credential reference then reads its repository like this:

1. With the host's App, when the remote is an `https` repository on the App's GitHub, using a token for that repository
   alone.
2. Where the App is not installed on that repository: with `SQLFLOW_GIT_TOKEN`, when the host has one.
3. Otherwise with no credential, which serves a public repository.

A configuration that is half there (an App ID without a key, a key without an App ID, or both key variables) is
refused with the variable to fix, never ignored.

`.sqlflow/env` holds one line per value, so on a developer's machine use `SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH`, or the
base64 of the `.pem` file in `SQLFLOW_GITHUB_APP_PRIVATE_KEY`.

### A reference on the repository source

Put the App in the source's **credential reference**, the same field that otherwise names a token:

```text
${githubapp:app=Iv23liAbCdEf0123;key=keyvault:my-vault/github-app-private-key}
```

Store the key in the vault first:

```bash
az keyvault secret set --vault-name my-vault --name github-app-private-key --file acme-sqlflow-reader.private-key.pem
```

The reference travels with every run, so a worker resolves it with its own identity and needs no variable of its own.
A reference that names no repository is narrowed to the source's own repository and to the access each operation
needs. Its settings, separated by `;`:

| Setting | Value |
|---|---|
| `app` | The App's client ID or App ID. Given with `key`, or both left out to use the host's App. |
| `key` | Where the private key is kept, written without its `${ }`: `keyvault:<vault>/<secret>` or `env:<NAME>`. |
| `repository` | `owner/name`. Left out on a repository source: the source's own repository. |
| `installation` | The installation's numeric id, when the App should not look it up from the repository. |
| `permissions` | `name:level` pairs separated by commas, such as `contents:read`. Left out: what the operation needs. |
| `api` | The API root of GitHub Enterprise Server or GHE.com. |

An scm flow can name an App in `repository.secret` the same way; its pushes ask for `contents:write`.

## 4. Check it

- On the **Repos** page, **Git credentials** shows this control plane's App and token, and **Check with GitHub** asks
  GitHub, as the App, for its name, its owner and where it is installed.
- **Test access**, on a repository's page and in the register dialog, reads the branch tip with the credential a sync
  would use, and says which credential that is.
- Every sync's trace says which credential it read with.
- The same checks over the API: `GET /api/v1/repos/credentials`, `POST /api/v1/repos/credentials/github-app/check` and
  `POST /api/v1/repos/check`.

## Rotating the key and removing access

- **Rotate**: generate a new private key on the App's page, put it where the old one was (the vault secret, the
  variable or the file), then delete the old key on GitHub. A process asks GitHub for a new token at most every ten
  minutes, so the new key is in use within minutes.
- **Remove access**: remove the repository from the installation, or uninstall the App. The next sync is refused.

## When something goes wrong

| The message says | Why | What to do |
|---|---|---|
| `GitHub App ... is not installed on owner/name` | The App has no installation reaching the repository. | Install the App on the repository (step 2). |
| `refused the signed token of GitHub App ... (HTTP 401)` | The client ID and the key do not belong together, the key was deleted on GitHub, or the host's clock is wrong. | Check the client ID, put the App's current key in place, set the host's clock right. |
| `is not granted contents:write,pull_requests:write` | A proposal asked a read-only App to push. | Grant the App Contents and Pull requests read and write, or propose by hand. |
| `is suspended` | An owner suspended the installation. | Unsuspend it under the organization's **Settings**, **GitHub Apps**. |
| `is rate limiting GitHub App ...` | GitHub asked for a wait longer than a request waits. | Nothing: the next sync asks again. |
| `the GitHub App private key ... is encrypted`, `is a public key`, `is not a PEM key` | The key is not the `.pem` file GitHub generated. | Store the downloaded `.pem` file as it is. |

No message, log line or trace carries the private key, the signed request or a token.

## The full test tier's App

The full test tier can prove this against real GitHub with a test App made by the steps above: read-only, installed on
a small private repository holding at least one file. It reads `SQLFLOW_TEST_GITHUB_APP_ID`, the key in
`SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY_PATH` or `SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY`, and
`SQLFLOW_TEST_GITHUB_REPOSITORY` (`owner/name`), from the environment or `.sqlflow/env`, and writes nothing to the
repository. The test App is optional, as a GitHub App is for a deployment that reads with a token: with none of these
set, the live suite is skipped, saying which to set; with only some of them, it fails, naming the rest.
