# Product

## Register

product

## Users

Users managing official accounts and custom API profiles for Codex and Claude on Windows, macOS, and Linux. Windows/macOS provide a desktop settings window; a separate CLI shares the Rust core. Opening the GUI always shows connection settings; saving and launching are separate actions.

## Product Purpose

Keep each managed client on the connection the user chose. For Codex that means launching ChatGPT with its optional OpenAI cloud endpoints disabled for the Electron shell, while leaving the child Codex app server and custom API provider untouched. For Claude it means writing or clearing the `ANTHROPIC_*` environment variables in `settings.json` without disturbing official login credentials. The two clients are independent: Codex can stay on an official account while Claude uses a custom API. Only the Launch button starts the selected client. The launcher exits after a successful launch request and stays open on failure so the user can retry.

## Brand Personality

Calm, restrained, trustworthy.

## Anti-references

No promotional splash screen, fake percentage, decorative animation, modal workflow, or launcher-level single-instance behavior.

## Design Principles

- Always open settings without launching the client.
- Keep saving separate from launching; unsaved changes block launch.
- Keep the two clients' drafts independent and switch between them without losing edits.
- Do not add startup splash screens, countdowns, window polling, or global launch shortcuts.
- Close only after a successful explicit launch request; leave errors in settings for retry.
- Preserve native application behavior, including repeated launches.
- Keep the custom API path independent from Electron shell networking.

## Accessibility & Inclusion

Use readable system typography, sufficient contrast, a standard progress control for explicit history repair, clear status text, keyboard-safe behavior, and reduced motion when Windows animations are disabled.
