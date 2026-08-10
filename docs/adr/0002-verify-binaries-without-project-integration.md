# Verify loaded binaries without project integration

RWL defines launch success as proof that Rhino loaded the expected `.rhp` and critical dependencies from their exact selected-worktree artifact paths. A bundled RWL verifier produces this proof, and launched plug-ins are not required to expose authentication, initialization, callback, or receipt-writing behavior; this deliberately trades plug-in-specific readiness guarantees for a zero-integration, low-friction project boundary. ADR 0010 changes artifact ownership without changing this verification rule.
