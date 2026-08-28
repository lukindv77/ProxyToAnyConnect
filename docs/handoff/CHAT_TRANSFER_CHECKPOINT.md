# Chat transfer checkpoint — 2026-08-28

Canonical startup prompt: `docs/handoff/NEW_CHAT_PROMPT.md`.

Accepted production code baseline before this docs commit:
- main `5811900dfbf7488bd8ac53af20348c462681eeef`;
- tree `e44bf16408da3abade0c0f4d04708e6fd5ccd4ac`;
- exact build #616 / `33152272544` green, artifact `9678213447`, digest `sha256:bd31b7f143d11c56cfc6794e55760e156341ca07bdd8fcbb52691d5010e9c1e7`;
- exact handoff #393 / `33152272516` green, artifact `9678172387`, digest `sha256:bef544b5997914274001b50fce35684dcdd633d44c6230de654c0769db0a77c9`.

Latest completed blocks: #79 outbound deadline/504; #80 client-header 408; #85 terminal exact cleanup owner retention + one real application-shutdown retry. Earlier deterministic hardening through #52–#77 is also closed completed; live issue comments are authoritative lineage.

Open external/architecture boundaries are exactly #2/#4/#5/#6/#7/#11/#13 at this checkpoint.

This docs commit moves `main`. A new chat must fetch live main/tree and exact-head `build`/`handoff` first, then continue broad deterministic audit/development without fabricating real Windows/L2TP or soak evidence.
