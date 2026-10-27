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

**Setup:** Copy `appsettings.example.json` to `appsettings.json`, then fill in the Hasura endpoint and admin secret. `appsettings.json` is git-ignored and gets embedded in the build; `Services/Hasura/HasuraSettings.cs` reads it. Never commit the secret or put it back into source. The project has no test projects. Code style is set in `.editorconfig` (suggestion-level only, so it never breaks the build); `dotnet format FamilyFeud.csproj --no-restore` works on this project and should leave no changes. In Debug builds, Blazor WebView dev tools are enabled.

## Issue tracking

Known bugs and requested features are listed in `TODO.md`. When you finish an issue, move its row from the Open table to the Completed table, with the date and a short note on the fix. Add newly found problems as new rows in the Open table.

## Architecture

- **Hosting:** `App.xaml.cs` creates a `Window` with `MainPage.xaml`, whose `BlazorWebView` mounts `Components/Routes.razor` (the router, with default layout `Components/Layout/MainLayout.razor`).
- **Two windows, one state:** `Game.razor.cs` toggles a second MAUI `Window` that contains `Components/Pages/AudiencePage.xaml`. That page has its own `BlazorWebView`, which mounts `Audience.razor` directly as its root component (it doesn't go through the router). Both windows resolve the same singleton `GameStateService`, which is how the audience display stays in sync. Components subscribe to `GameState.StateChanged` and call `InvokeAsync(StateHasChanged)`. They must unsubscribe in `Dispose`.
- **`Services/GameStateService.cs`** contains all of the game rules: teams, scores, the current question, revealed answers, strikes, and turn control. It works as a state machine over `GameMode` (the `Mode` property):
  - `PlayOff`: the face-off. Each team gets one `FirstAnswer`. If a team finds the top answer, or once both teams have answered, `PlayoffAwaitingChoice` is set.
  - `Normal`: after the host calls `ChoosePlay()` or `ChoosePass()`, revealed answers add to the round pool for the team in control. Three `HostStrike()` calls move the game to `PointSteal`.
  - `PointSteal`: the other team gets one guess. A correct reveal wins them the pool plus that answer. A strike gives the pool to the team in control.
  - **Points pool:** reveals in `PlayOff` and `Normal` add to `RoundPool`; team scores don't change on reveal. The private `EndRound(winner)` adds the whole pool to the winner's score, empties the pool, sets `IsRoundFinished` and notifies once. It runs when every answer is revealed in `Normal` (the control team wins), when Play or Pass is chosen after the face-off already revealed every answer (the team now in control wins, e.g. a one-answer question), or when a steal succeeds or fails. Once `IsRoundFinished` is set, reveals are display-only and strikes are ignored until the next question. `ResetPlayoffState` empties the pool, so a round abandoned by loading a new question loses it. `RevealAllAnswers` on the current question mid-round abandons the round too (private `AbandonRound`): the pool is discarded, no score changes, `IsRoundFinished` is set and `RoundsPlayed` doesn't count it; if no questions are left the game ends with `OutOfQuestions`. After a finished round, Reveal All is display-only.
  - **Game length (#9, #46):** the start screen has two independent checkboxes and calls `SetGameLength(int? totalRounds, int? targetScore)`; null means that limit is off. At least one must be set (both null throws `ArgumentException`), rounds must be one of `RoundOptions` (5/10/15/20) and the target must be positive. `EndRound` increments `RoundsPlayed` and sets `IsGameOver` at whichever comes first: a team's score ≥ `TargetScore`, `RoundsPlayed` ≥ `TotalRounds`, or every cached question asked. If one round hits both limits, `EndReason` is `TargetScore`. Once `IsGameOver`, `SetCurrentQuestion` ignores new questions. `GameProgressText` (both windows) shows whichever limits are set: "Round 2 of 10", "First to 300 points (round 2)" or "Round 2 of 10 · First to 300 points". `Winner` gives the leading team (`Random` for a tie or before the end). `ResetGame()` clears progress and restores the defaults (5 rounds, no target score; `DefaultTargetScore` 300 is only the start screen's initial box value).
  - **Winning screen (#8):** `EndRound` also sets `EndReason` (`GameEndReason.RoundsPlayed`, `TargetScore` or `OutOfQuestions`). `WinnerHeadline` ("Winner: <name>" / "It's a tie!") and `GameOverReasonText` ("After 5 rounds", "<name> reached 300 points after 3 rounds", "Out of questions after 2 rounds") are shared by both windows. When `IsGameOver`, `Game.razor` shows a winner panel (`.ff-winner*` in `master.css`) above the last round's board, with a **New game** button that calls `GoBack()`; `Audience.razor` replaces the whole board with a large winner screen (`.ff-winner-audience`).
  - `SetCurrentQuestion` resets the face-off state, switches `StartingTeam` for every question after the first (not when called with `null`), and gives the face-off turn to `StartingTeam`. This is the only place the starting team alternates. The start screen sets `StartingTeam` from the host's pick, or `ChooseRandomFirst()` picks one. `.github/request.md` lists the intended gameplay rules. Check it before changing game logic.
  - `ResetGame()` (called by the host's Back button) clears scores, strikes, pool, mode, current question, revealed answers, turn, `StartingTeam` and team names, and notifies once. The `Questions` cache is kept; the Game page rebuilds its remaining-questions pool from it.
- **Hasura backend:** `Services/Hasura/` contains `IHasuraService`/`HasuraService`, which send raw GraphQL query/mutation strings over `HttpClient`. It also holds the DTOs (`Question`, `Answer`, with JSON names that match the Hasura columns `question`, `answer`, `points`, `question_id`). `Answer.Blank` (Id -1) is a sentinel value used for face-off strikes. The client is set up in `MauiProgram.cs`.
- **Pages** (`Components/Pages`): `/` Home, `/startgame` team setup, `/game` host board, `/questions` and `/new-question[/{id}]` for question CRUD. The Home page reads its random adjectives and colours from `Resources/adjectives.xml`, which is an embedded resource.

## Conventions (from `.github/copilot-instructions.md`)

- Pages use partial-class code-behind: markup goes in `X.razor` and logic goes in `X.razor.cs`. Services are injected with `[Inject]` in the code-behind, and navigation uses `NavigationManager.NavigateTo`. (`Audience.razor` is an exception: it uses `@inject` and an inline `@code` block.)
- Register services in `MauiProgram.CreateMauiApp()`.
- Use Bootstrap utility classes. Scoped CSS goes in `X.razor.css` next to the component.
- **Buttons (#45):** every button uses the shared classes in `master.css`: `ff-btn` (secondary, the default), plus `ff-btn-primary` (the main action, gold accent) or `ff-btn-danger` (deletes), and `ff-btn-sm` for buttons in lists, table rows and alerts. Put side-by-side buttons in a `ff-btn-row` (one gap everywhere); a modal's buttons go in `<div class="ff-btn-row ff-modal-actions">` (right-aligned, Cancel before the main action). Don't use Bootstrap `btn`/`btn-primary`/`btn-outline-*` or add per-page button styles. (Answer tiles, the who-goes-first options and the picker's list items aren't buttons in this sense and keep their own styles.)
- **Never use `@import` in scoped `*.razor.css` files.** The SDK rejects them. Shared styles and CSS variables belong in `wwwroot/css/master.css`, which `wwwroot/index.html` links (the project sets `EnableDefaultCssItems=false`).
- Get the user's agreement before changing `TargetFrameworks` or `UseMaui`, or moving away from the BlazorWebView hosting model.
- Code style (encoded in `.editorconfig`): explicit types instead of `var` everywhere, C# and `@foreach` in markup alike (`var` only for anonymous types); block-scoped namespaces; Allman braces; 4-space indent; `using` directives instead of fully qualified type names; PascalCase public members and `_camelCase` private fields. In `GameStateService`, constants, fields, the event and all properties come first, then the constructor, then methods. Nullable reference types are enabled.
