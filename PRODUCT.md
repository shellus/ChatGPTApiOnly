# Product

## Register

product

## Users

Users launching the Microsoft Store ChatGPT/Codex desktop application with a custom API provider. They need immediate visual acknowledgment after a double-click while the Electron application starts.

## Product Purpose

Launch ChatGPT with its optional OpenAI cloud endpoints disabled for the Electron shell, while leaving the child Codex app server and custom API provider untouched. Success means the launcher responds immediately, communicates startup progress, and disappears when the ChatGPT window is available.

## Brand Personality

Calm, restrained, trustworthy.

## Anti-references

No promotional splash screen, fake percentage, decorative animation, modal workflow, or launcher-level single-instance behavior.

## Design Principles

- Acknowledge every launch immediately.
- Reflect real application state instead of showing a fixed-duration animation.
- Stay visually quiet and get out of the way when ChatGPT is ready.
- Preserve native application behavior, including repeated launches.
- Keep the custom API path independent from Electron shell networking.

## Accessibility & Inclusion

Use readable system typography, sufficient contrast, a standard progress control, clear status text, keyboard-safe behavior, and reduced motion when Windows animations are disabled.
