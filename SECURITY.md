# Security policy

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting for this repository
(**Security → Report a vulnerability**). You will get an answer within a week.

## Supported versions

Tarol is proprietary and for the owner's use only (see LICENSE); there are no public releases.

## Agent skills in packages

Tarol packages ship agent skills (`skills/**/SKILL.md`) that coding agents read and act on. A skill never asks an
agent to download or run anything, to change credentials or settings, or to disable analyzers. Report any skill
that does as a vulnerability.
