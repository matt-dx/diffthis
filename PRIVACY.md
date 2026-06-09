# Privacy Policy for DiffThis

_Last updated: 9 June 2026_

DiffThis ("the app") is a Windows desktop application for comparing git
branches and obtaining AI-assisted code review and explanation. This policy
explains what data the app handles, where it goes, and what it does not do.

The developer of DiffThis ("we", "us") does not operate any server,
account system, or analytics backend. DiffThis runs entirely on your computer.

## Summary

- DiffThis has no developer-operated servers and collects no analytics or
  telemetry.
- Your repositories and source code stay on your machine, except for the diff
  text you explicitly choose to send to a third-party AI provider.
- Credentials for AI providers are stored locally on your machine.
- When you use a cloud AI provider (Claude or GitHub Copilot), your diff text
  is sent to that provider and is governed by their privacy policy.
- When you use a self-hosted Ollama endpoint, your data is sent only to the
  endpoint you configure — which can be entirely offline on your own hardware.

## Data DiffThis processes

### Source code and diffs
DiffThis reads local git repositories that you open. The resulting diffs are
displayed and processed on your machine. Source code is never transmitted
anywhere unless you explicitly request an AI review or explanation, in which
case only the diff content (capped and truncated to a maximum size) and basic
diff metadata (file names, change counts, detected languages) are sent to the
AI provider you have selected.

### AI provider credentials
To use a cloud AI provider, DiffThis stores authentication credentials locally:

- **GitHub Copilot** — OAuth and session tokens are stored in Windows
  SecureStorage on your device.
- **Claude** — DiffThis reads credentials written by the Claude Code CLI
  (`~/.claude/.credentials.json`); token handling is performed by that CLI.
- **Ollama** — any optional API key you configure for an endpoint is stored in
  the app's local settings.

These credentials are used solely to authenticate your requests to the
respective provider. They are not transmitted to us and we never receive them.

### Application settings and cache
DiffThis stores preferences (such as theme, selected models, per-repository
branch selections, and configured Ollama endpoints) and an AI response cache
locally on your machine, under your user profile's local application data
folder. This data never leaves your device and can be cleared by deleting the
app's local data.

## Third-party AI providers

When you choose to run an AI review or explanation, your diff content is sent
to the provider you select. Your use of those providers is governed by their
own terms and privacy policies:

- **GitHub Copilot** — https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement
- **Anthropic Claude** — https://www.anthropic.com/legal/privacy
- **Ollama** — data is sent only to the endpoint URL you configure. If you run
  Ollama locally, this data does not leave your machine.

We do not control, and are not responsible for, the data practices of these
third parties. Please review their policies before sending data to them.

## What we do not do

- We do not collect, store, or transmit your source code to ourselves.
- We do not operate analytics, telemetry, or crash-reporting services.
- We do not sell or share any personal data.
- We do not require you to create an account with us.

## Children's privacy

DiffThis is a developer tool and is not directed to children. It does not
knowingly collect any information from children.

## Changes to this policy

We may update this policy from time to time. Changes will be published at this
document's URL, and the "Last updated" date above will be revised.

## Contact

For questions about this privacy policy, please open an issue at
https://github.com/matt-dx/diffthis/issues.
