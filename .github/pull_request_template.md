<!--
  CONTRIBUTE.md defines the isolation contract and the evidence each section needs.
  Keep the headings; a body that does not follow this structure is returned without review.
-->

## Summary

<!-- One to three sentences: what changes and what problem it solves. -->

## Related issue

<!-- Fixes #<issue>, or "No issue — typo/link fix". Security fixes cite the advisory instead. -->

## Changes

<!-- One line per area, e.g.
- RVM/: revoke the serial-device ACE when startup fails after the grant
- RVM/tests/: add the failure-path case
-->

## Verification

<!-- Commands run and their actual output. Screenshots for UI changes.
     Session and audit evidence for isolation changes. See CONTRIBUTE.md → Build and verify. -->

## Isolation impact

<!-- "None", plus one sentence why no ACL, pipe, policy, or grant code was touched.
     Otherwise: the boundary, the grant, its default state, and the test proving the denied case still denies. -->

## Risk and rollback

<!-- What can break, for whom, and how to revert, including data written outside the repository. -->

## Checklist

- [ ] Relevant build and tests pass (see CONTRIBUTE.md)
- [ ] Isolation contract is unchanged, or the change is a documented strengthening
- [ ] Regenerated `RWebView/dist/` committed (frontend changes)
- [ ] `natives/win-x64/` rebuilt and committed with toolchain versions (C++ changes)
- [ ] Docs updated in both languages where behavior or setup changed
- [ ] No secrets, tokens, session volumes, logs, or local paths in the diff or pasted output
- [ ] Commits are rebased; no merge commits
