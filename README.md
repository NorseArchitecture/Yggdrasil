# Yggdrasil

> The world tree, whose branches and roots bind all nine realms together.

![Yggdrasil — the immense world tree whose roots reach into the underworld and whose branches cradle the nine realms](https://github.com/user-attachments/assets/49b591e9-87c8-4d2a-a5ba-3d33dc3a15c2 "Yggdrasil — the world tree whose branches and roots bind all nine realms together")

*Image credit: [@norsemythologyclips](https://www.instagram.com/norsemythologyclips/) — go follow them.*

Connective tissue for the Norse Architecture — **`Norse.Hosting`**: the web, worker, and migration service chassis (`Norse.Hosting.Web.Server`/`.Web.Client`/`.Web.Components`/`.Worker`/`.Migrations.Service`/`.Stories.Client`/`.Stories.Server`) and the deployables built on it. It hosts the runtime endpoints that Bifröst composes but never provides itself. In the dependency chain it rides on Midgard and everything below; Himinbjörg and Heimdall ride above it.

## Status

**`Hosting.Migrations.Service` is live** — the first real deployable in this realm, part of the platform-wide migrations framework proven end to end across six realms (the full story is on [Bifröst's README](https://github.com/NorseArchitecture/Bifrost#readme)). Its `Program.cs` is three lines calling the source-generated `AddNorseMigrations()` from Urðarbrunnr; it runs against a real Postgres database (`norse_identity`) and exits clean. **`Hosting.Stories.Client`/`.Stories.Server` are also live** — the BlazingStory catalog host for Bragi's `DesignSystem.Stories`, container-publishable via `dotnet publish /t:PublishContainer` and published to `ghcr.io/norsearchitecture/hosting/stories`. `Hosting.Web.Server`'s Identity story has moved: the `Account`/`Manage` Razor page tree that used to live here now lives in Heimdall's `AuthN.Components`/`.FluentUI` (login, register, logout) and Himinbjörg's `Identity.Web.Server` (external login, manage, passkeys); this project just wires them together via Himinbjörg's `AddNorseAuthenticationService`, with real persistence against Postgres `norse_identity` — the old `PlaceholderUserStore` is gone. That wiring, plus the gRPC bridge for `IAuthenticationService` (in-process and browser wire gateways), is in progress on `feature/hosting-web-server-authn`, not yet on `master`. `Hosting.Web.Components` carries real template pages plus routing/theme wiring, and `Hosting.Web.Client` now carries real gRPC-Web client wiring too (cookie-credentialed channel to `IAuthenticationService`). Only `Hosting.Worker` remains a genuine minimal stub — placeholder code, passing tests, container-publishable the same way — until Asgard and Midgard ship the hosting abstractions it's waiting on. Each subsequent type surface follows the spec-first discipline: brainstorm → spec → plan in [Glitnir](https://github.com/NorseArchitecture/Glitnir)'s `docs/Yggdrasil/`, greenlit by the human, then code.

## The cosmos

Yggdrasil is one realm of the [Norse Architecture](https://github.com/NorseArchitecture). The whole platform composes at [Bifröst](https://github.com/NorseArchitecture/Bifrost) — clone once, cross the bridge, and every session starts there so decisions get brainstormed across the entire landscape, not in isolation. Every design is tried in [Glitnir](https://github.com/NorseArchitecture/Glitnir), the design court, before code is forged here; this realm's specs and plans will live in the court's [docs/Yggdrasil/](https://github.com/NorseArchitecture/Glitnir/tree/master/docs/Yggdrasil) once they converge.

## Soundtrack: Yggdrasil
[![Soundtrack: Yggdrasil](https://img.youtube.com/vi/v5yYMjU8xDg/maxresdefault.jpg)](https://www.youtube.com/watch?v=v5yYMjU8xDg)
