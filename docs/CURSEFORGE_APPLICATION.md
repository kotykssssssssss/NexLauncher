# CurseForge application — local draft for owner review

Prepared: 20 October 2026. **NOT SUBMITTED.** No form has been filled, no API key
has been requested, and no agreement has been accepted on the owner's behalf.
The owner must review every statement and supply their own identity/contact details.

## Existing project

Project name: **NexLauncher** (within the form's stated 30-character limit).

Supported game: **Minecraft: Java Edition**. Current target platform: Windows x64.
macOS Intel/Apple Silicon and Linux x64 preview packages are now cross-published.
Linux launcher startup and fixture checks passed on Ubuntu 24.04/WSLg without
an installed .NET SDK. macOS startup and Minecraft launches remain untested. Their MSAL browser
sign-in requires the owner's registered/approved Client ID, which is not yet
available. Do not claim production-ready Microsoft login on macOS/Linux.

Declared source repository: https://github.com/kotykssssssssss/NexLauncher

Proposed project page: the public README at the same repository. The owner must
verify public availability and confirm that this is acceptable for the Website URL
field. No separate website or public release was created during this preparation.

Current license: MIT for launcher source. This does not relicense Minecraft,
mod files, skins or third-party artwork. Rights to distributed assets require the
owner's review. Do not claim a complete independent intellectual-property audit.

## Suggested answers — review before copying into the form

### Project purpose and supported features

NexLauncher is an independent Windows desktop launcher and instance manager for
Minecraft: Java Edition, written in C# with Avalonia and .NET. It manages separate
Vanilla, Fabric, Forge and NeoForge instances, Java and memory settings, installation
and repair, and Microsoft and clearly labelled local/offline profiles. It currently
supports Modrinth mods and modpacks, local diagnostics, session statistics,
instance import/export and user themes. CurseForge integration is not implemented.
We are requesting permission and technical clarification before adding it.

### Reason for development

I am developing a desktop tool for organizing multiple Minecraft installations and
maintaining modded instances. Separate game folders, compatible mod versions,
repeatable imports and local diagnostics are its main uses. The application has
functionality independent of a mod catalog. CurseForge would be an additional
content source integrated into the existing instance manager.

### Planned funding

**Yes — potentially through optional external donations/fundraising.**

### Funding details

The current launcher is free to use and has no implemented ads, subscriptions,
paid features or payment processing. I may add an optional external link for
voluntary support of launcher development. The provider and URL have not been
selected; GoFundMe or another service is being considered. These contributions
would support the launcher developer. They would not be presented as donations
to mod authors, and access to mod files would not be sold.

Owner review: confirm this remains accurate at submission time. Disclose any
additional advertising, subscriptions, paid features or other monetization plans.

### Support for creators

The existing Modrinth project pages show author attribution and links to original
projects. Managed mod exports store exact project/version references rather than
bundling those JAR files. Exporting local files requires the user to confirm that
they have checked the rights to share them; this is not an automatic license check.

For a future approved CurseForge integration, I propose showing attribution and
original project links, respecting authors' third-party distribution choices,
using only authorized download responses, and avoiding mirrors or rehosting.
Author-provided support links could be displayed where explicitly available.
Optional donations to NexLauncher would be kept separate from author support.
Please confirm whether this approach meets your author-support requirements or
whether another arrangement is required. No revenue-sharing or financial
contribution to mod authors is currently implemented or promised.

### Additional notes / questions requiring written clarification

Before implementation, please clarify:

1. Whether this independent Minecraft launcher and its proposed catalog integration
   are acceptable under the provision concerning competing products.
2. The approved API-key deployment model for an open-source distributed desktop
   client. We will not publish or borrow an API key. We are not proposing an
   unapproved proxy as a workaround.
3. Which metadata, exact project/file identifiers and installed-file records may
   be stored locally, and what, if any, response caching is permitted. The existing
   Modrinth HTTP cache will not automatically be reused for CurseForge.
4. Applicable request limits, bandwidth arrangements and required attribution.
5. Whether the disclosed optional developer donations and author-support approach
   are acceptable, and whether any further account/ownership restrictions apply.

Microsoft profiles use the existing authentication flow with game ownership
checking. Local/offline profiles are explicitly separate; they do not claim
ownership or bypass online-mode server authentication. This feature is disclosed
and has not been hidden or removed for the application.

## Owner-only fields and decisions

These are intentionally **not filled**:

- Real name, nickname, email and full Discord username.
- Final Website URL and confirmation that Git URL is publicly accessible.
- Final donation provider/link and any other monetization intentions.
- Confirmation of rights to all material distributed with the application.
- Confirmation of legal capacity to enter the agreement.
- Acceptance of the API terms, author-distribution acknowledgement and bandwidth
  acknowledgement. Only the owner may review and make those confirmations.

There is no guarantee of approval. A draft or passing test suite is not provider
authorization. Do not claim official partnership, approval, active CurseForge
support, absence of all monetization, or implemented author revenue-sharing.

## Local evidence available for review

- [Project features](../README.md), [privacy notice](../PRIVACY.txt),
  [source license](../LICENSE), [current release checks](RELEASE_VALIDATION.md).
- Microsoft ownership checker: `Services/Authentication/WindowsMinecraftAuthenticationBackend.cs`.
- Local/Microsoft separation: `Services/AccountService.cs`.
- Managed-mod references and local-file allowlist: `Services/Instances/InstanceExportService.cs`
  and `Services/Instances/InstanceManifestService.cs`.
- Export rights confirmation: `ViewModels/InstanceTransferViewModel.cs`.
- Credential protection and logs: `Services/Authentication/ProtectedAccountVault.cs`
  and `Services/LauncherLog.cs`.
- No CurseForge provider, key or automated request has been added.

## Official sources consulted

- [API application process](https://support.curseforge.com/support/solutions/articles/9000208346-about-the-curseforge-api-and-how-to-apply-for-a-key)
- [Application form linked by CurseForge](https://forms.monday.com/forms/dce5ccb7afda9a1c21dab1a1aa1d84eb?r=use1)
- [API terms](https://support.curseforge.com/support/solutions/articles/9000207405-curseforge-3rd-party-api-terms-and-conditions)
- [Author distribution preferences](https://support.curseforge.com/support/solutions/articles/9000207877-project-distribution-toggle)
- [Official REST API](https://docs.curseforge.com/rest-api/)
