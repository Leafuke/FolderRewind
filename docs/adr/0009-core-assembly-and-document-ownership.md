---
status: accepted
---

# Core shares production compilation while the Host owns application orchestration

Source-linked tests avoid WinUI but maintain a second compilation graph and substitute configuration and application types. Core gives history and headless policies one production owner; the application supplies configuration snapshots and platform adapters. Core remains Windows-oriented and may use Runtime, while Runtime never references Core and plugins continue to reference only Abstractions.

Normal tests reference the production Core assembly. Fault-injection recovery tests and probes use a separately isolated build of the same project; these hooks must not enter application packages. UI and application-bound source links remain explicit until their boundary can be separated without changing behavior.

The Host repository retains domain terms, accepted decisions and versioned safety/format specifications. User and developer guides belong to the bilingual website. Plans, progress and run evidence belong to ignored artifacts, with Git history preserving removed historical documents.
