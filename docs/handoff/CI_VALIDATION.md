# Current main validation

This branch exists only to trigger the pull-request Windows validation pipeline for current `main` because GitHub App content-API commits have not been producing `push` workflow runs.

Validation base when created: `4502a1531e365e44e49375fd5f72284ea003eb0b`.

The marker does not change product/runtime behavior. The pull request must validate the complete inherited code, including HTTP framing close hygiene, transactional proxy startup ownership, native asynchronous ICMP keepalive lifecycle, and Windows integration evidence smoke coverage.
