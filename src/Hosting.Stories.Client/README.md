# Hosting.Stories.Client

The WASM host for Bragi's `DesignSystem.Stories` content, served in production by `Hosting.Stories.Server` via `UseBlazorFrameworkFiles()`.

## Why this has two HTML documents, not one

`wwwroot/index.html` and `wwwroot/iframe.html` are **two independent browser documents**, each bootstrapping its own WASM runtime instance (both end in the same `_framework/blazor.webassembly...js` boot script). `iframe.html` is the document BlazingStory loads inside an `<iframe>` to render an isolated story canvas — sandboxed on purpose, so a story's own CSS/JS can't bleed into the outer shell chrome and vice versa. This is BlazingStory's own architecture, not something this repo chose.

**This cannot become a single `App.razor` + `IFrame.razor` pair.** A `.razor` file is a component the WASM runtime mounts into a `<div>` *after* one of these two documents has already loaded — it isn't a browser-servable document itself. There's no way to collapse two boot entry points into one without breaking BlazingStory's isolation mechanism. Don't relitigate this without first checking whether BlazingStory's own hosting model has changed upstream.

## Why this doesn't look like `Hosting.Web.Client`/`.Server`

That pair is a server-rendered Blazor Web App with `InteractiveWebAssembly` render mode on individual components — one root document (`Components/App.razor`, rendered server-side), no static `index.html` anywhere in source. This project is genuinely standalone client-side WASM (the classic "hosted Blazor WebAssembly" template), which is why it has `wwwroot/index.html` at all. The two pairs solve different problems; matching folder/naming conventions where they're actually comparable is fine, but the HTML document count isn't one of those places.
