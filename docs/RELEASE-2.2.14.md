# Solaris Neon 2.2.14 — UI refinement

This release changes only the Solaris home screen presentation, following the owner's annotated launcher screenshot.

- Remove the redundant **Новости** tab from the top navigation. The lower Solaris News panel and **Читать** action remain working.
- Raise the **БОЛЬШЕ ЧЕМ MINECRAFT** subtitle 15 WPF units toward the main Solaris logo.
- Reveal unblurred original approved Vanilla and Modded art through the top 62% of each server card, fading smoothly back into the shaded details zone over the next 6% of its height.
- Keep the existing hover-to-sharp transition, button actions, original card images, game launches, accounts, and automatic updates unchanged.

## Validation

- Windows DEV workflow r191 passed before release promotion.
- Stable release triggers on the `solaris-2.2-dev` branch via a `release:` commit.
- No change to the independently managed modpack, server connectivity or Solaris ID backend.
