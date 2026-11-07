# Sprint Timer

A small Windows overlay for tracking Swift Start in ARC Raiders.

It watches the ability icon and displays a timer with an optional warning. Sprint input can start an estimate before the icon appears. Once the icon is confirmed, detection controls whether the benefit is considered active or ended.

The icon decides; the timer advises.

## Running it

Use the compiled application package, extract the whole folder somewhere writable, and run `SprintTimerIntegrated.exe`.

Keep these files together:

- `SprintTimerIntegrated.exe`
- `SDL2.dll`
- `icon.png`
- `detector.json`

Your preferences are saved in `settings.json` beside the application. That file is local to your setup and is not included in the source repository.

This is a Windows x64 application built against .NET Framework. Exact runtime prerequisites and clean-machine setup still need to be documented before a wider release.

GitHub's automatic source-code ZIP is not a compiled application package.

## Setup

1. Choose Keyboard or your controller.
2. Click Assign, press your sprint input, then release it.
3. Match Hold or Toggle to your in-game sprint setting.
4. Select the monitor showing ARC Raiders.
5. Select your Swift Start rank.
6. Choose a rank-based warning or set a manual reminder time.
7. Position the overlay, then minimize the settings window.

Controller Toggle also observes left-stick direction. Returning the stick to neutral clears its remembered sprint state.

Minimizing keeps monitoring running. Closing the main window saves settings and exits. Help can be reopened whenever you need it.

## Reading the bar

The bar fills from left to right against the estimated duration for the selected rank.

- Green: the timer is progressing.
- Orange: the warning threshold has been reached.
- Red: the icon's disappearance has been confirmed. The bar and final time are frozen.
- Dashed outline while running: timing is still estimated rather than icon-confirmed.

A full bar is not proof that the ability has ended. If the icon is still visible, the bar stays active even after the estimated duration.

The red result remains until a new timer starts or the session is cleared. Elapsed seconds can be shown centered underneath the bar or hidden entirely.

A capture error shows a muted empty bar and `--` instead of declaring that the ability ended.

## Reminders

Rank-based mode subtracts your warning lead from the selected rank's estimated duration. The default lead is two seconds.

The current estimates for ranks 1 through 5 are 6.1, 7.6, 9.6, 11.1, and 13.6 seconds. These came from observations, not verified game constants.

Manual mode lets you choose the elapsed time at which the warning starts. Set the manual value to zero to disable the warning. The selected rank still determines the bar's scale.

Neither reminder mode ends or resets the session.

## A few things to know

ARC Raiders must be the foreground application. Switching away clears the session rather than pausing it. Opening Help or Debug also takes focus away from the game.

The ability HUD can be absent during the beginning of an activation. An initial estimated timer is expected and should not be mistaken for failed detection.

The overlay hides if it overlaps the detector's search band. Move it rather than disabling that protection.

Resolution, aspect ratio, Windows scaling, and monitor arrangements can affect capture and detection. The current tests do not establish support for every configuration.

## Troubleshooting

Start with the selected game monitor and assigned sprint input.

Debug contains recognition information, session reset, text logging, and diagnostic-image capture. Logging and image capture are separate options, and both start off.

If reporting a problem, include:

- Application version.
- Game resolution and display mode.
- Windows scaling setting.
- What the icon actually did.
- What the timer did differently.
- Relevant diagnostics, if collected.

A timer log alone cannot establish whether the ability was actually active.

## Anti-cheat

The app observes screen pixels and keyboard/controller input. It does not inject into the game, read game memory, or generate game input. It does not use AutoHotkey.

Anti-cheat approval has not been established. Screen capture and a click-through overlay are not guarantees of approval or ban safety.

## Development

The application is written in C# with WinForms. `Build.ps1` handles staged x64 compilation, version checks, verified backups, and installation.

The source repository does not include the compiled EXE or personal runtime settings. Historical experiment tools and validation records are retained, but some tools depend on ignored input snapshots and will not run from a fresh clone without those files.

Earlier development notes sometimes call the ability Fresh Start. Those references mean Swift Start.
