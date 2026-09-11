# Product

## Register

product

## Users

Users managing official accounts and custom API profiles for the Microsoft Store ChatGPT/Codex desktop application. Opening the launcher always shows connection settings; users save configuration and explicitly launch the client as separate actions.

## Product Purpose

Launch ChatGPT with its optional OpenAI cloud endpoints disabled for the Electron shell, while leaving the child Codex app server and custom API provider untouched. Only the Launch button starts the client. The launcher exits after a successful launch request and stays open on failure so the user can retry.

## Brand Personality

Calm, restrained, trustworthy.

## Anti-references

No promotional splash screen, fake percentage, decorative animation, modal workflow, or launcher-level single-instance behavior.

## Design Principles

- Always open settings without launching the client.
- Keep saving separate from launching; unsaved changes block launch.
- Do not add startup splash screens, countdowns, window polling, or global launch shortcuts.
- Close only after a successful explicit launch request; leave errors in settings for retry.
- Preserve native application behavior, including repeated launches.
- Keep the custom API path independent from Electron shell networking.

## Accessibility & Inclusion

Use readable system typography, sufficient contrast, a standard progress control for explicit history repair, clear status text, keyboard-safe behavior, and reduced motion when Windows animations are disabled.
