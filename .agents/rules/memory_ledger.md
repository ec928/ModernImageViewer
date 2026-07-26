# Mandatory Project Memory & Architecture Ledger Rule

Whenever working within this project or codebase, you MUST adhere to the following memory and documentation laws:

1. **Maintain a Living Ledger**: At the start of any substantive task, check for (or create) an authoritative architecture and progress ledger in the primary project or subsystem directory (e.g., `ARCHITECTURE.md` or `PROJECT_MEMORY.md`).
2. **Consult Before Acting**: Always read this ledger file before proposing architectural modifications or refactors. You must never violate established design invariants, repeat documented historical failures, or regress completed features.
3. **Record All Progress**: Before completing a major task, bug fix, or design change, you MUST update the ledger file. Record:
   - What was accomplished, files modified, and commit hashes.
   - Any new architectural rules or interaction laws established.
   - Known limitations or technical debt owed for future work.
4. **Prevent Memory Drift**: Never rely solely on short-term conversation transcript history. Treat the physical ledger file on disk as your single, authoritative source of truth across all sessions and subagents.
