# Repository Agent Instructions

## Mandatory active plan

This repository is executing a full WPF-to-WinUI 3 migration. Before inspecting, editing, building, or testing the project, every agent must read and follow:

- [WINUI3_MIGRATION_PLAN.md](./WINUI3_MIGRATION_PLAN.md)

The plan is the single source of truth. Do not create a competing migration plan.

The plan records the approved technical direction, but it does not by itself authorize product-code changes. Follow the user's current request. If the current request is plan-only, edit documentation only; do not start implementation, restore, build, test, run, or cleanup merely because unchecked items exist.

## Checklist workflow

For every implementation step:

1. Claim the step in the plan's **Current work** table before editing code.
2. Work only on a step whose dependencies are complete, unless the plan explicitly allows parallel work.
3. Do not mark a checkbox `[x]` until the implementation and the listed verification are both complete.
4. When completing a step, add verification evidence to the plan's **Progress log**, then remove the claim from **Current work**.
5. If blocked, leave the checkbox unchecked and record the blocker; never mark partial work complete.
6. Update the plan in the same change whenever implementation changes an architectural decision, file layout, dependency, or acceptance criterion.
7. A claim is valid only while that agent is actively working on the step. Remove it before ending or pausing the work; keep unfinished status in **Awaiting verification**, not as a stale claim.

## Non-negotiable migration boundaries

- Final product: one WinUI 3 application process with a WinUI `BrowserWindow` and WinUI `ControlWindow`.
- Migrate the whole UI; no permanent WPF/WinUI hybrid and no permanent companion process.
- Use the standard WinUI 3 `WebView2`; do not use WPF `WebView2CompositionControl`, `D3DImage`, or `GraphicsCaptureSession` hosting.
- Browser opacity is whole-window HWND alpha. Preserve the current continuous 10%-100% opacity behavior; do not copy Snap.Hutao's activate/deactivate opacity switching unless the user explicitly changes the requirement.
- At 100% opacity, remove/avoid the layered-window transparency path.
- Preserve the existing WebView2 profile, settings, history, favorites, downloads, hotkeys, theme, language, and release behavior unless the plan explicitly says otherwise.
- `E:\testcode1\Snap.Hutao.Remastered-main` is a read-only architectural reference. Do not add a dependency on its private native package, DI framework, source generators, or application code.
- Temporary coexistence of the legacy WPF project and the new WinUI project is allowed only during migration. The final acceptance phase removes WPF.

## Worktree safety

At plan creation, `MainWindow.xaml.cs` already contains a user-approved 51-line deletion that removes the old low-memory strategy. Treat it as pre-existing user work and do not revert it accidentally.
