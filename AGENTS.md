# Project instructions

## Authoring dashboard

Amethyst uses the web dashboard at `https://accessibilitymods.com/owner/` on
the VPS to manage and publish her mods. The desktop AuthorTool is for
third-party developers; updating it does not update Amethyst's dashboard.

The web dashboard source is in the sibling repository
`../AccessibilityModsServer/OwnerDashboard`. Its operational documentation is
`../AccessibilityModsServer/docs/owner-dashboard.md`, and its update procedure
is implemented by `../AccessibilityModsServer/deploy/update-owner-dashboard.py`.
It references this repository's shared `AccessibilityModManager.Authoring`
project. Changes to authoring behavior may need shared-library changes, web
form changes, and a verified VPS deployment. Report local changes and deployed
changes accurately.

## Accessible chat formatting

Amethyst is blind and navigates assistant messages by heading with a screen reader.
Headings are an accessibility requirement for responses that contain distinct sections.

- Use real Markdown `##` headings for separate topics, including explanations,
  setup instructions, completed work, testing steps, and questions.
- A phrase such as "To authorize it" introduces a section. Write it as a heading,
  for example `## Authorize the Key`, with the instructions underneath.
- Bold labels, bullets, and introductory sentences do not replace semantic headings.
- Use `## Completed` for completed work and `## Need Your Input` when an answer
  is required. Put the question immediately beneath its heading.
- Keep paragraphs short. Use numbered lists for sequential steps and short bullet
  lists for parallel items. Leave a blank line after headings and before lists.
- Very short, single-topic acknowledgements and progress updates need no heading.
- Before sending a multi-section response, check that each section can be reached
  through the screen reader's heading navigation.

Communicate directly in chat. Do not create question, status, or testing-handoff
files unless Amethyst explicitly requests them.
