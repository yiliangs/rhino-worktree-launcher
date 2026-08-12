# 0011: Use one installable payload producer

## Status

Accepted

## Context

Source-checkout installation and release packaging independently built and published the desktop, CLI, MCP, bootstrap, and verifier binaries. Maintaining two producers allowed their commands, output layout, and contents to diverge even though both ultimately installed the same application shape.

## Decision

`eng/New-RwlPackage.ps1` is the only producer of the installable payload. A source-checkout install asks that producer for a temporary package and then follows the same payload-consumption path as a downloaded package. CI may still compile and test the solution for verification, but those builds do not define distributable binaries.

## Consequences

- Source installation and release packaging share one output layout and one set of publish commands.
- The installer owns installation state, stable bootstrap replacement, shortcuts, and client integration, but no binary compilation.
- Source installation incurs temporary package staging and removes it after copying the payload into the versioned release directory.
