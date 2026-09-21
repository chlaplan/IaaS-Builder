# Mission Landing Zone (vendored)

`mlz.json` is the pre-compiled ARM template from Microsoft's Mission Landing Zone, copied here
verbatim and unmodified.

| | |
|---|---|
| Upstream | https://github.com/Azure/missionlz |
| Source path | `src/mlz.json` (compiled from `src/mlz.bicep`) |
| Retrieved | 2026-09-18 from branch `main` |
| Size | 727,202 bytes |
| SHA-256 | `39B80CC456BF407F1654ECE0D9A751220222884644FC3151D423D5E189EB8AC6` |
| Licence | MIT - see `LICENSE` in this folder |

## Why a vendored copy rather than a URL

This tool has to run inside air-gapped enclaves, so nothing may be fetched at deploy time. The
compiled template is self-contained, which was verified rather than assumed - it contains **no**
`templateLink`, `_artifactsLocation`, `fileUris`, `deploymentScripts`, `containerSettings` or
`raw.githubusercontent.com` references. Every Bicep module is inlined as a nested deployment with
an embedded template body, and the PowerShell artifacts (`New-KeyVaultKey.ps1`,
`New-ADDSForest.ps1`, `Remove-VirtualMachine.ps1`) are embedded via `loadTextContent` and run
through the VM guest agent rather than downloaded.

## Why pinned by hash rather than by release tag

The only GitHub Release, `v1.0.0`, was published 2024-04-19, while `main` is actively developed
(last push 2026-09-11). The release is over two years behind the tree, and upstream's own
documentation points at `src/mlz.json` on `main` rather than at the release asset. So the copy is
pinned by content hash instead. Verify it with:

```powershell
(Get-FileHash Templates\MLZ\mlz.json -Algorithm SHA256).Hash
```

## Refreshing it

```powershell
curl.exe -sL -o Templates\MLZ\mlz.json https://raw.githubusercontent.com/Azure/missionlz/main/src/mlz.json
```

Then re-run the air-gap check above, update the size and hash in this file, and run the test suite:
`MissionLandingZoneTests` reads the real template and will fail if a parameter this tool binds has
been renamed or has had its allowed values changed upstream.

## Deployment notes that are easy to get wrong

* **Subscription scope.** The `$schema` is `subscriptionDeploymentTemplate.json`, so this cannot be
  deployed into a resource group. MLZ creates its own resource groups, one per tier.
* **One required parameter.** `identifier`, 1-5 alphanumeric characters. Everything else defaults.
* **Single subscription is fine.** `hubSubscriptionId`, `identitySubscriptionId`,
  `operationsSubscriptionId` and `sharedServicesSubscriptionId` all default to the deployment
  subscription.
* **Do not supply** `windowsVmAdminPassword`, `linuxVmAdminPasswordOrKey` or `deploymentNameSuffix`
  unless the corresponding VM is being deployed. Their defaults use `newGuid()` / `utcNow()`, which
  are only legal as top-level parameter defaults.
* **Prerequisites** upstream documents: Owner on the target subscription, and the *Encryption At
  Host* feature registered. Azure also caps subscription diagnostic settings at five.
