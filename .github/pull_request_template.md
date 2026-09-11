## What this changes

<!-- One or two sentences. What problem does this solve? -->

## What I verified

<!-- Be literal. "dotnet test passed, 99 tests" or "not run, CI only". Do not write "tests pass" unless you ran them. -->

- [ ] `dotnet build BDIT.TenantToolkit.sln -c Release` succeeds
- [ ] `dotnet test tests/BDIT.TenantToolkit.Tests -c Release` passes
- [ ] Tested against a live tenant (say which, and what happened)

## Safety

- [ ] No existing test was changed to make a suite pass. If one was, explain why the test was wrong.
- [ ] No safety invariant in AGENTS.md was weakened.
- [ ] Behaviour touching an invariant has a test that would fail without the change.
- [ ] No tenant evidence, client ID, token or client name is included.

## What I did not do

<!-- Known gaps, anything left unproven, anything needing live-tenant validation. This section is not optional. -->
