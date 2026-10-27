# Copilot Instructions for FamilyFeud (MAUI + Blazor)

This repository is a .NET MAUI app that hosts a Blazor WebView. The intent is a Family Feud game implemented as Blazor components running inside a MAUI shell.

## Top-level facts
- MAUI host: see `MauiProgram.cs` (register services, add BlazorWebView). The app uses `BlazorWebView` inside `MainPage.xaml` to mount Blazor UI.
- Blazor router: `Components/Routes.razor` is the central router; pages live under `Components/Pages` and layouts under `Components/Layout`.
- Static assets & CSS: `wwwroot/index.html`, `wwwroot/app.css`, and `wwwroot/lib/bootstrap/...` control the client UI styles.
- Target frameworks: project targets .NET 10 (multiple platforms) as configured in `FamilyFeud.csproj`.

## Codebase conventions you must follow
- Razor + code-behind: pages use partial class code-behind files next to the `.razor` file (example: `Components/Pages/Home.razor` + `Components/Pages/Home.razor.cs`). Keep UI markup in `.razor` and interaction logic in the partial `.cs` file.
- Navigation: use `NavigationManager` injected with `[Inject]` in the code-behind to call `NavigateTo("/route")`. See `Home.razor.cs` for the pattern.
- Components organization: keep pages under `Components/Pages`, shared layout/components under `Components/Layout`, and register any DI services in `MauiProgram.CreateMauiApp()`.
- Styling: prefer BootStrap utility classes (already referenced). Keep per-layout CSS files next to `.razor` (see `MainLayout.razor.css`).
- Important CSS rule: do NOT use `@import` inside scoped component CSS files (any `*.razor.css`). The build/runtime cannot guarantee a stable loading order for `@import` in scoped CSS and the SDK rejects such files. Place shared/global styles and CSS variables in a non-scoped global stylesheet under `wwwroot/css/master.css` and reference it from `wwwroot/index.html`.

## Build & run workflows
- Primary development environment: Visual Studio (run the MAUI app via the standard debug target). Use the platform dropdown to choose Android/iOS/Windows.
- CLI: `dotnet build` and `dotnet run` for platform-specific RID are supported; use Visual Studio for device/emulator workflows.
- Debugging Blazor components: run in Debug in Visual Studio; `builder.Services.AddBlazorWebViewDeveloperTools()` is enabled in `MauiProgram.cs` when `DEBUG`.

## Where to add integrations and services
- Backend/API clients: register `HttpClient` and any backend service in `MauiProgram.cs` via `builder.Services.AddSingleton(...)` or `AddHttpClient(...)`. Inject services into Blazor components using `[Inject]`.
- Secure secrets: do not hard-code credentials. Use platform secure storage or inject a secure configuration provider before creating clients.
- Offline support / sync: add local persistence (SQLite or file) as a service and register it in `MauiProgram` for reuse by pages.

## Routing & page patterns
- Declare routes in `.razor` pages with `@page "/yourroute"`. The router in `Components/Routes.razor` discovers pages via assembly scanning.
- Back button pattern: implement a `GoBack()` method in the code-behind and call `Navigation.NavigateTo("/")` or use history APIs if desired.

## Files to inspect for examples
- `Components/Pages/Home.razor` + `Components/Pages/Home.razor.cs` — shows button-driven navigation.
- `Components/Pages/StartGame.razor` + `.razor.cs` and `Components/Pages/Questions.razor` + `.razor.cs` — examples of page + back button.
- `MauiProgram.cs` — DI and BlazorWebView registration.
- `MainPage.xaml` — how Blazor is hosted inside MAUI via `BlazorWebView`.
- `FamilyFeud.csproj` — platform targeting and MAUI settings.

## What NOT to change without discussion
- Changing project `TargetFrameworks` or `UseMaui` without confirming platform requirements.
- Removing `BlazorWebView` hosting model; the app is built around MAUI + Blazor hosting.

If anything is unclear or you need more instruction examples (for adding a new service, API client, or a page pattern), say which area and examples will be added or expanded.