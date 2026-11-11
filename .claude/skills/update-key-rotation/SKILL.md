---
name: update-key-rotation
description: Rotate or revoke the ECDSA P-256 key that signs BYDLO's update catalog (releases-catalog.json, issue #411). Use for planned rotation, a suspected key compromise, or when the BYDLO_UPDATE_SIGNING_KEY secret was lost. Never touches system networking.
---

# update-key-rotation

The update catalog is signed in CI with the repository secret `BYDLO_UPDATE_SIGNING_KEY` (PKCS#8 PEM).
Clients trust only the public keys compiled into `src/Bydlo.Core/Updates/UpdateTrustRoots.cs`:
`CurrentKeyId`/`CurrentPublicKeySpki`, the rotation slot `NextKeyId`/`NextPublicKeySpki`, and `RevokedKeyIds`.
`scripts/sign-update-catalog.ps1` signs as `CurrentKeyId` and **refuses** a secret whose public key is not
`CurrentPublicKeySpki`, so a release can never publish a catalog that installed clients reject.

A client verifies with the trust root of the version it is running. A new key therefore has to ship
inside a release **before** any catalog is signed with it.

## Ground rules

- The private key never enters the repository, a commit, a PR, a log, chat or an issue. Generate it outside
  the checkout (`-OutputDirectory` pointing outside the repo, e.g. the agent scratchpad), store it with
  `gh secret set BYDLO_UPDATE_SIGNING_KEY < key.pem`, then delete the file. `gh secret set` changes repository
  settings: do it only when the maintainer asked for it in chat.
- GitHub secrets are write-only. Once the local PEM is deleted, the secret is the only copy.
- Key ids are `bydlo-update-YYYY-NN` and are never reused, not even after revocation.
- `scripts/new-update-signing-key.ps1` needs PowerShell 7 (`pwsh`). Without it, generate the pair with an
  equivalent throwaway .NET program outside the repo (`ECDsa.Create(ECCurve.NamedCurves.nistP256)`,
  `ExportSubjectPublicKeyInfo()` → base64 SPKI, `ExportPkcs8PrivateKeyPem()` → PEM). Do not download tools for it.
- Follow the normal git flow from `CLAUDE.md`: issue → branch from `developer` → PR into `developer`.
- Verify with the `safe-check` skill. CI's "Update catalog interop" step proves the script and `Bydlo.Core`
  agree and that a non-matching key is refused.

## Planned rotation (two releases)

1. **Generate** the new pair: `pwsh scripts/new-update-signing-key.ps1 -KeyId bydlo-update-YYYY-NN -OutputDirectory <outside repo>`.
   Keep the PEM outside the repo until step 5.
2. **Release N: pre-publish the key.** Set `NextKeyId` to the new id and `NextPublicKeySpki` to the printed SPKI.
   Leave `Current*` and the secret unchanged. CI still signs with the old key, and clients on N trust both keys.
3. **Wait** until most installations run N or newer. Clients older than N only know the old key; after
   step 4 they can still update by hand, but not automatically.
4. **Release N+1: switch.** In one PR, move the new id/SPKI into `CurrentKeyId`/`CurrentPublicKeySpki` and
   clear `Next*` (`NextPublicKeySpki = ""`, `NextKeyId` = the id reserved for the following rotation).
   Add the old id to `RevokedKeyIds` only if it must never verify again; a planned rotation may leave it out.
5. **Swap the secret** after that PR is merged and **before** tagging N+1:
   `gh secret set BYDLO_UPDATE_SIGNING_KEY < new.pem`, then delete `new.pem`. If you tag first, the release job
   stops with "does not match UpdateTrustRoots.CurrentPublicKeySpki". That is the guard working: swap the
   secret, then re-run the job.
6. Update the key ids in `docs/CODE-SIGNING.md` if the doc names them.

## Compromised key (emergency)

1. Generate a new pair as in step 1.
2. One release: new key in `Current*`, compromised id in `RevokedKeyIds`, `Next*` cleared. Swap the secret
   before tagging (step 5).
3. Clients on that release or newer reject anything signed with the compromised key. Older clients still trust
   it, and the catalog has no online revocation, so tell users in the release notes to update by hand.
   Do not claim more than that.

## Lost secret (key not compromised)

Treat it as a planned rotation. Until N+1 is released no catalog can be signed: tag releases are blocked by
the guard, while manual `workflow_dispatch` rehearsals still sign with a throwaway key that no client trusts.

## Checklist before merging a rotation PR

- [ ] Only `UpdateTrustRoots.cs` (and docs) changed; no PEM, no private key text anywhere in the diff.
- [ ] `CurrentPublicKeySpki` decodes to a P-256 SPKI and matches the key that will be in the secret.
- [ ] `safe-check` is green; CI's "Update catalog interop" step is green.
- [ ] The release notes for a compromise tell users on older versions to update by hand.
