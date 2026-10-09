# Patreon releases

## Supported workflow

The CLI keeps Patreon campaign and tier metadata. For third-party authors, the wrapped ZIP is attached to a Patreon post manually. The manager checks the user's entitlement, opens the post in the browser, and asks the user to select the downloaded ZIP. It then verifies the package hash and manifest before installation.

No custom download server is configured through the CLI. Do not put a paid ZIP on a public GitHub release.

## API limitation

Patreon's documented v2 post API does not expose downloadable post attachments. Patreon retired public API v1 on October 7, 2026, with temporary extensions for approved creators. The CLI does not rely on the old v1 attachment lookup. See [Patreon's API documentation](https://docs.patreon.com/#post-v2).

`patreon post validate --url URL` validates URL syntax and extracts the post ID. It does not prove that the post exists, that its tiers match, or that the attachment was uploaded. Those checks require your browser and a real manager test.

## Prepare the post

1. Build and validate the wrapped ZIP with `package build`.
2. Upload that exact ZIP to a post in your Patreon campaign. Configure the post's audience.
3. Record its HTTPS post URL and the exact attachment filename.
4. Obtain your campaign and tier IDs through `patreon login`, `patreon status --json`, and `patreon tiers --json`, or your existing verified configuration.

Tier IDs are not ordered by price. List every tier that should grant access; do not infer "this tier and higher" from numeric IDs.

## Publish the catalog entry

```sh
amm-author patreon post validate --url https://www.patreon.com/posts/example-release-123456 --json
amm-author release publish --game example-game --version 1.0.0 --channel beta --zip ./packages/example-game-1.0.0.zip --asset-destination patreon-post --patreon-gate ./patreon-gate.json --project ./catalog --dry-run
amm-author release publish --game example-game --version 1.0.0 --channel beta --zip ./packages/example-game-1.0.0.zip --asset-destination patreon-post --patreon-gate ./patreon-gate.json --project ./catalog --yes
```

Use `docs/examples/patreon-gate.json` as the input shape. `postId` is the numeric ID extracted from the URL. Together with that ID, the manager uses the canonical `https://www.patreon.com/posts/ID` address. Include `campaignId`, the allowed `tierIds`, and `attachmentFileName`.

The CLI computes SHA-256 from your local ZIP and publishes the metadata to the GitHub catalog. It does not upload the ZIP to Patreon or claim to have downloaded and checked the remote attachment. Uploading a different ZIP produces a hash failure when users try to install it.

## Test access and installation

Use an entitled account to check that the release appears, opens the correct post, and installs the exact downloaded package. Check that an unentitled account cannot install it through the manager. Verify that an incorrect file is refused. Creator previews are not a substitute for testing the patron's experience.
