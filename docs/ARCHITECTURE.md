# Architecture

## Goal

Make a face on the wallpaper look at the cursor, but only when the wallpaper is what the user is looking at, without ever interfering with another program.

## Stack

C# on .NET 8, WPF for the UI and for rendering, Win32 through P/Invoke for the desktop integration. WPF's retained-mode compositor is hardware accelerated, so moving a transform is nearly free and Direct2D is not needed. Published as a self-contained single-file exe.

## Getting behind the icons

Explorer draws the wallpaper in a window behind the icon list. Sending `Progman` the undocumented message `0x052C` makes it create a `WorkerW` window for that purpose. A window parented to that `WorkerW` is part of the wallpaper layer.

| Windows build | Layout | What Gaze does |
| --- | --- | --- |
| Windows 10, Windows 11 up to 23H2 | The `WorkerW` that follows the one hosting `SHELLDLL_DefView` | Enumerate top-level windows, take the next `WorkerW` |
| Windows 11 24H2 and later | `WorkerW` is a child of `Progman` | Take `Progman`'s child `WorkerW` |

Gaze creates one `HwndSource` per monitor with `WS_CHILD`, `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE` and a `WM_NCHITTEST` hook returning `HTTRANSPARENT`. The window is positioned with `ScreenToClient` so negative virtual-screen coordinates (a monitor left of or above the primary) work. The scene is laid out in device pixels, so per-monitor DPI scaling never blurs the photo.

## Why the overlay draws the whole photo

A child window cannot be partly transparent over the real wallpaper, so each overlay draws the cover-fitted photo itself and the eyes on top. The same photo is also set as the real Windows wallpaper, which is what you see when tracking is off.

## Drawing the eyes

The photo already contains eyes, so each marked eye is covered by a synthetic one built from the photo's own colours:

- sclera, iris and skin colour are sampled around the marked eye (`ColorSampler`),
- the eye opening is an ellipse clip, rotated to follow the line between the eyes,
- the iris, pupil and catch-light sit in a layer that is only translated,
- a lid-shadow gradient and an edge vignette hide the seam,
- an optional blink lid in the sampled skin colour.

Sizes are stored as multiples of the distance between the eyes, so moving a marker rescales the whole eye consistently.

## Gaze model

`GazeMath` is pure and unit tested.

1. The cursor vector is rotated into the eye's own frame.
2. `Target` maps distance through `tanh`, so a nearby cursor makes a small move, a far one reaches the limit and the pupil can never leave the eye ellipse.
3. A critically damped spring (`SmoothDamp`) chases the target. It is smooth, has no overshoot and lags by roughly 130 ms at the default setting.
4. Both eyes aim at the cursor from their own position, so they converge naturally when the cursor is close to the face.

## Knowing when the desktop is visible

`WallpaperHost` runs a 250 ms probe. For every eye it calls `WindowFromPoint` on the eye's screen position, climbs to the root window and checks the class name: `Progman` or `WorkerW` means the desktop, anything else means an app is on top. Lock and unlock, display changes, resume from sleep and an Explorer restart are handled.

- Visible: `FrameClock` hooks `CompositionTarget.Rendering` and ticks at most about 60 times a second.
- Covered: the hook is removed. No frames, no cursor polling.

## Windows limits

- `0x052C` and the `WorkerW` layout are undocumented. They have changed before (24H2) and may change again.
- Parenting across processes needs matching DPI awareness. Gaze enables mixed DPI hosting before creating the windows.
- The desktop wallpaper layer is not reachable while the secure desktop (UAC prompt, lock screen) is shown. The probe sees no desktop and pauses.
- Windows may reset `WorkerW` when the theme or wallpaper changes. The probe notices the lost parent and re-attaches.
- Other programs' overlays can register as covering the desktop.

## Not verified here

The code compiles, the logic is unit tested and CI builds the exe on a Windows runner. The behaviour of the overlay on real hardware (multi-monitor, mixed DPI, 24H2) depends on Explorer internals and needs testing on actual machines. Open an issue with your Windows build number if something is off.
