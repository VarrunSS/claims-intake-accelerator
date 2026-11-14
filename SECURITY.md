# Security policy

This is a portfolio reference implementation for a fictional customer. It is not deployed anywhere and holds no real data, but the code is
written to be reused, so a weakness in it matters.

## Reporting a vulnerability

Please report it privately, not in a public issue. Use **Report a vulnerability** on this repository's Security tab (GitHub private
vulnerability reporting). Include what you found, where (file and line, or the request), and how to reproduce it. This is a one-person
project, so there is no response-time guarantee, but a report will be read and answered.

## What is in scope

- A secret, key, token or personal data committed to the repository or its history.
- A way around authentication, the app roles or the segregation-of-duties rule ([ADR 0009](docs/adr/0009-identity-roles-and-segregation-of-duties.md)), or a write to the claims system without a recorded approval.
- A way for document content to steer the agent past the citation guard or the route floor ([threat model](docs/threat-model.md)).
- A weakness in the supply-chain controls (workflows, dependencies, lock files).

Known gaps are already listed and are not new findings: see [Known limitations](docs/verification-status.md), for example the in-memory review
store, public endpoints and the Prompt Shields seam that does nothing yet.

## Supported versions

Only the `main` branch.
