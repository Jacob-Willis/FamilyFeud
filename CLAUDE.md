# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A Family Feud game built as a single-project .NET 10 MAUI app hosting a Blazor WebView (`FamilyFeud.csproj`, solution `FamilyFeud.slnx`). The host runs the game on one window and can open a second "Audience" window for a projector. Questions and answers are stored in a remote Hasura GraphQL backend.

## Build & run

Visual Studio is the main dev environment (choose the platform target from the debug dropdown). From the CLI, build or run one target framework at a time. On this Windows machine that's usually:

```
dotnet build -f net10.0-windows10.0.19041.0
dotnet run -f net10.0-windows10.0.19041.0
```

Other TFMs: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`.

**Setup:** Copy `appsettings.example.json` to `appsettings.json`, then fill in the Hasura endpoint and admin secret. `appsettings.json` is git-ignored and gets embedded in the build; `Services/Hasura/HasuraSettings.cs` reads it. Never commit the secret or put it back into source. The project has no test projects and no lint configuration. In Debug builds, Blazor WebView dev tools are enabled.

## Issue tracking

Known bugs and requested features are listed in `TODO.md`. When you finish an issue, move its row from the Open table to the Completed table, with the date and a short note on the fix. Add newly found problems as new rows in the Open table.

## Architecture

- **Hosting:** `App.xaml.cs` creates a `Window` with `MainPage.xaml`, whose `BlazorWebView` mounts `Components/Routes.razor` (the router, with default layout `Components/Layout/MainLayout.razor`).
- **Two windows, one state:** `Game.razor.cs` toggles a second MAUI `Window` that contains `Components/Pages/AudiencePage.xaml`. That page has its own `BlazorWebView`, which mounts `Audience.razor` directly as its root component (it doesn't go through the router). Both windows resolve the same singleton `GameStateService`, which is how the audience display stays in sync. Components subscribe to `GameState.StateChanged` and call `InvokeAsync(StateHasChanged)`. They must unsubscribe in `Dispose`.
- **`Services/GameStateService.cs`** contains all of the game rules: teams, scores, the current question, revealed answers, strikes, and turn control. It works as a state machine over `GameMode`:
  - `PlayOff`: the face-off. Each team gets one `FirstAnswer`. If a team finds the top answer, or once both teams have answered, `PlayoffAwaitingChoice` is set.
  - `Normal`: after the host calls `ChoosePlay()` or `ChoosePass()`, revealed answers add to the round pool for the team in control. Three `HostStrike()` calls move the game to `PointSteal`.
  - `PointSteal`: the other team gets one guess. A correct reveal wins them the pool plus that answer. A strike gives the pool to the team in control.
  - **Points pool:** reveals in `PlayOff` and `Normal` add to `RoundPool`; team scores don't change on reveal. The private `EndRound(winner)` adds the whole pool to the winner's score, empties the pool, sets `IsRoundFinished` and notifies once. It runs when every answer is revealed in `Normal` (the control team wins), or when a steal succeeds or fails. Once `IsRoundFinished` is set, reveals are display-only and strikes are ignored until the next question. `ResetPlayoffState` empties the pool, so a round abandoned by loading a new question loses it.
  - `SetCurrentQuestion` resets the face-off state, switches `StartingTeam` for every question after the first (not when called with `null`), and gives the face-off turn to `StartingTeam`. This is the only place the starting team alternates. The start screen sets `StartingTeam` from the host's pick, or `ChooseRandomFirst()` picks one. `.github/request.md` lists the intended gameplay rules. Check it before changing game logic.
  - `ResetGame()` (called by the host's Back button) clears scores, strikes, pool, mode, current question, revealed answers, turn, `StartingTeam` and team names, and notifies once. The `Questions` cache is kept; the Game page rebuilds its remaining-questions pool from it.
- **Hasura backend:** `Services/Hasura/` contains `IHasuraService`/`HasuraService`, which send raw GraphQL query/mutation strings over `HttpClient`. It also holds the DTOs (`Question`, `Answer`, with JSON names that match the Hasura columns `question`, `answer`, `points`, `question_id`). `Answer.Blank` (Id -1) is a sentinel value used for face-off strikes. The client is set up in `MauiProgram.cs`.
- **Pages** (`Components/Pages`): `/` Home, `/startgame` team setup, `/game` host board, `/questions` and `/new-question[/{id}]` for question CRUD. The Home page reads its random adjectives and colours from `Resources/adjectives.xml`, which is an embedded resource.

## Conventions (from `.github/copilot-instructions.md`)

- Pages use partial-class code-behind: markup goes in `X.razor` and logic goes in `X.razor.cs`. Services are injected with `[Inject]` in the code-behind, and navigation uses `NavigationManager.NavigateTo`. (`Audience.razor` is an exception: it uses `@inject` and an inline `@code` block.)
- Register services in `MauiProgram.CreateMauiApp()`.
- Use Bootstrap utility classes. Scoped CSS goes in `X.razor.css` next to the component.
- **Never use `@import` in scoped `*.razor.css` files.** The SDK rejects them. Shared styles and CSS variables belong in `wwwroot/css/master.css`, which `wwwroot/index.html` links (the project sets `EnableDefaultCssItems=false`).
- Get the user's agreement before changing `TargetFrameworks` or `UseMaui`, or moving away from the BlazorWebView hosting model.
- Code style: explicit types are preferred over `var` in services, and nullable reference types are enabled.
