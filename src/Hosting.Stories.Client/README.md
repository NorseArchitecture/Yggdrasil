# Hosting.Stories.Client

The WASM host for Bragi's `DesignSystem.Stories` content, served by `Hosting.Stories.Server` via `MapStaticAssets()` + `MapFallbackToFile("index.html")`.

## `Microsoft.AspNetCore.Components.WebAssembly.DevServer` is not cruft

This project references `Microsoft.AspNetCore.Components.WebAssembly.DevServer` (`PrivateAssets="all"`). It looks unused — nothing in source calls it directly — but removing it breaks `dotnet run` on `Hosting.Stories.Server`: ASP.NET Core only wires a referenced WASM project's static web assets (`index.html` included) into the host's `WebRootFileProvider` when running, which is what makes `MapFallbackToFile("index.html")` resolve anything. Without the package, `GET /` 404s and the console warns `The WebRootPath was not found` — `Hosting.Stories.Server` has no physical `wwwroot` of its own by design; everything comes from this project via static web assets. It's also a prerequisite for `Hosting.Stories.Server`'s `UseWebAssemblyDebugging()` call. Got excised once already (2026-07-12, `aa53619`) on the assumption it was leftover template scaffolding — it isn't; restored the same day.

Relatedly, `Hosting.Stories.Server` needs `ASPNETCORE_ENVIRONMENT=Development` to load static web assets at all (see its `Properties/launchSettings.json`) — that auto-wiring is Development-only by ASP.NET Core convention, regardless of this package.

## Why this has two HTML documents, not one

`wwwroot/index.html` and `wwwroot/iframe.html` are **two independent browser documents**, each bootstrapping its own WASM runtime instance (both end in the same `_framework/blazor.webassembly...js` boot script). `iframe.html` is the document BlazingStory loads inside an `<iframe>` to render an isolated story canvas — sandboxed on purpose, so a story's own CSS/JS can't bleed into the outer shell chrome and vice versa. This is BlazingStory's own architecture, not something this repo chose.

**This cannot become a single `App.razor` + `IFrame.razor` pair.** A `.razor` file is a component the WASM runtime mounts into a `<div>` *after* one of these two documents has already loaded — it isn't a browser-servable document itself. There's no way to collapse two boot entry points into one without breaking BlazingStory's isolation mechanism. Don't relitigate this without first checking whether BlazingStory's own hosting model has changed upstream.

## Why this doesn't look like `Hosting.Web.Client`/`.Server`

That pair is a server-rendered Blazor Web App with `InteractiveWebAssembly` render mode on individual components — one root document (`Components/App.razor`, rendered server-side), no static `index.html` anywhere in source. This project is genuinely standalone client-side WASM (the classic "hosted Blazor WebAssembly" template), which is why it has `wwwroot/index.html` at all. The two pairs solve different problems; matching folder/naming conventions where they're actually comparable is fine, but the HTML document count isn't one of those places.
