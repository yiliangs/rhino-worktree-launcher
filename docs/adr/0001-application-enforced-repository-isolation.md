# Keep registered repositories application-read-only

RWL treats registered repositories as user-approved read sources and places remote synchronization, source staging, dependency installation, build outputs, receipts, and diagnostics in its own application directory. This is an application-enforced contract rather than an OS sandbox: it avoids elevation and optional Windows virtualization features, accepting that deliberately malicious build code running as the user is outside the product's containment guarantee.
