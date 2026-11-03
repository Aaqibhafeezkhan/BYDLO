---
name: avalonia-desktop-ux
description: Designs and verifies accessible BYDLO desktop workflows in Avalonia AXAML. Use for navigation, connection states, responsive layouts, themes, animation, or visual review.
---

# Avalonia desktop UX

Read `CLAUDE.md` first. Pair this workflow with the local `frontend-design` skill when defining visual direction.

1. Walk the real tasks before changing a layout. Follow a first import through connect, disconnect and recovery. Follow server selection, tool activation, diagnostics and settings from their normal entry points. Preserve commands, platform availability, validation and existing data. Make the active VPN state and next action readable without opening another page. Distinguish independent tools from the VPN session.
2. Define the visual roles for navigation, content, selection, status and focus. Use dynamic theme resources for color. Test content-driven sizing with Russian and English strings, long server names, missing values and a narrow window. Keep the primary action visible while detail content scrolls. Use separators for adjacent data rows and reserve a distinct surface for a genuinely separate task or state.
3. Keep native Avalonia controls and automation peers. Bind localized `AutomationProperties.Name` to icon-only actions and label controls when their templates do not supply names. Keep visible keyboard focus, logical Tab order, Escape dismissal and focus restoration after dialogs. Pair color indicators with text or an icon. Aim for 4.5:1 contrast for normal text and 3:1 for meaningful control boundaries and focus indicators. Check actual foreground/background pairs in both base themes.
4. Reserve motion for state feedback. Prefer short brush, opacity or render transform transitions over animating size and reflow. Use the existing reduced motion setting, and keep focus, availability and command completion independent of animations. Provide a static equivalent for progress and status when motion is reduced. Check the APIs against the Avalonia version in `Directory.Packages.props`.

## Visual and interaction checks

Use the existing screenshot harness. Before .NET commands, load the environment script. Run the compiled app views in explicit Mock Mode with `BYDLO_SAFE_DEV=1` throughout.

```powershell
. ./scripts/env.ps1
dotnet run --project tools/Bydlo.Screenshots -c Release -- .tmp/desktop-ux-review ru --mock --verify-overhaul
```

Read the harness before relying on its coverage. Add specific assertions for changed interaction paths if the existing modes skip them. Verify the same flows with keyboard input and pointer input where applicable. Compare Light and Oled, desktop and compact, default and reduced motion. Inspect PNGs at native size for cut text, hierarchy, alignment, inconsistent states and contrast. Record the locale, width, theme and state with each finding. Fix the finding and regenerate the affected frames. A Skia frame shows what the app draws. Native window chrome, assistive technology and OS-specific behavior need a separate Mock Mode desktop check.

## References

- [Avalonia accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility) and [keyboard focus](https://docs.avaloniaui.net/docs/input-interaction/focus)
- [Avalonia transitions](https://docs.avaloniaui.net/docs/graphics-animation/control-transitions)
- [Avalonia 11 headless frame capture](https://v11.docs.avaloniaui.net/docs/concepts/headless/)
- [W3C text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html) and [non-text contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html)
