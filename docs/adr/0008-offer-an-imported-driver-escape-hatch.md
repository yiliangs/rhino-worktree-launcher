# 0008: Offer an imported-driver escape hatch

## Status

Superseded by ADR 0010

## Context

The default typed build profile should cover recognized project structures with minimal setup. Some users will need build behavior that RWL's typed steps do not yet express. Requiring them to place a support driver in every repository would recreate the intrusive integration this architecture removes.

## Decision

Each app-owned build profile will have one build-mode discriminator: the default typed recipe or an advanced imported driver. When a user selects a driver file, RWL will copy it into application-owned storage and will not depend on the selected source file afterward. RWL will execute the imported copy with the app-owned workspace as its working context and require it to return the expected plug-in and critical-dependency artifacts through an RWL-defined contract.

## Consequences

- Add Project always offers an automatically proposed default before the escape hatch.
- Users can replace the default build behavior without adding an RWL file to their repository.
- Moving or deleting the originally selected driver does not break the registered project.
- Updating a driver requires an explicit re-import, making the active executable configuration visible and stable.
- Application-enforced isolation directs normal driver behavior into the RWL workspace but does not contain a deliberately malicious script running with the user's process rights.
