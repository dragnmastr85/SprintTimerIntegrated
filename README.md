# Sprint Timer

A small, standalone C# WinForms application for tracking Swift Start in ARC Raiders.

It watches the ability icon and displays elapsed time, a progress bar, and an optional warning. Sprint input can start an estimate before the icon appears. The first icon confirmation keeps that estimate running. After that, confirmed icon disappearance resets the session timer.

The icon decides; the timer advises.

## Running it

Download `SprintTimer-v0.2.16-windows-x64.zip` from the v0.2.16 release and extract the whole folder somewhere writable.

Keep these files together and run `SprintTimerIntegrated.exe`:

- `SprintTimerIntegrated.exe`
- `SDL2.dll`
- `icon.png`
- `detector.json`

Preferences are saved in `settings.json` beside the application. That file is local to your setup and is not tracked in the source repository.

This is a Windows x64 application built against .NET Framework. Exact runtime requirements and clean-machine setup have not been verified.

## Setup

1. Choose Keyboard or your controller.
2. Click Assign, press your sprint input, then release it.
3. Match Hold or Toggle to your in-game sprint setting.
4. Select the monitor showing ARC Raiders.
5. Select your Swift Start rank.
6. Choose a rank-based warning or set a manual reminder time.
7. Position the overlay and choose whether to show elapsed seconds underneath.
8. Minimize the settings window.

Controller Toggle also observes left-stick direction. Returning the stick to neutral clears its remembered sprint state.

Minimizing keeps monitoring running. Closing the main window saves settings and exits.

## Reading the bar

The bar fills from left to right against the estimated duration for your selected rank.

- Green: the timer is progressing.
- Orange: the warning threshold has been reached.
- Steady red: the app has confirmed that the icon disappeared. The session timer resets, while the overlay retains the final elapsed value and fill position.
- Dashed outline: the timer has started from sprint input, but the app has not yet confirmed the icon.

Reaching the estimated duration alone does not end the session or turn the bar red. Confirming the icon does not verify the estimate's original start time.

The red result remains until a new timer starts or the session is cleared. Elapsed seconds can be shown centered underneath the bar or hidden.

A capture error shows a muted empty bar and `--` rather than treating the error as icon disappearance.

## Reminders

Rank-based mode starts the warning at the selected rank's estimated duration minus your warning lead. The lead is configurable and defaults to two seconds.

The estimates for ranks 1 through 5 are 6.1, 7.6, 9.6, 11.1, and 13.6 seconds. These are empirical estimates, not verified game constants.

Manual mode lets you choose the elapsed time at which the warning starts. Set the manual value to zero to disable the warning.

The selected rank determines the bar's scale in both modes. Neither reminder mode ends or resets the session.

## Behavior and limitations

ARC Raiders must be the foreground application. Switching away clears the session rather than pausing it. Opening Help or Debug also takes focus away from the game.

The ability HUD can be absent at the beginning of an activation. An initial timer estimate is expected and is not, by itself, evidence of failed detection.

The overlay hides if it overlaps the detector's search band. Move the overlay away from that band.

Cross-resolution, aspect-ratio, Windows scaling, and monitor-arrangement reliability have not been established. Do not assume detection will work unchanged on every display setup.

## Troubleshooting

Start by checking the selected game monitor and assigned sprint input. Help can be reopened from the main window.

Debug contains recognition information, session reset, text logging, and diagnostic-image capture. Logging and image capture are separate options, and both are off at startup.

When reporting a problem, include:

- Application version.
- Game resolution and display mode.
- Windows scaling setting.
- What the icon actually did.
- What the timer did differently.
- Relevant diagnostics, if collected.

A timer log alone cannot establish whether the ability was actually active.

## Anti-cheat

The app observes screen pixels and keyboard/controller input. It does not use AutoHotkey, inject into the game, read game memory, or generate game input.

Anti-cheat approval has not been established. Screen capture and a click-through overlay are not guarantees of approval or ban safety.

## Development

The application is written in C# with WinForms. `Build.ps1` handles staged x64 compilation, version checks, verified backups, and installation.

The source repository does not include the compiled EXE or personal runtime settings. Historical experiment tools and validation records are retained, but some tools depend on ignored input snapshots and will not run from a fresh clone without those files.

Earlier development notes sometimes call the ability Fresh Start. Those references mean Swift Start.
