# Yggdrasil

> The world tree, whose branches and roots bind all nine realms together.

![Yggdrasil — the immense world tree whose roots reach into the underworld and whose branches cradle the nine realms](https://github.com/user-attachments/assets/49b591e9-87c8-4d2a-a5ba-3d33dc3a15c2 "Yggdrasil — the world tree whose branches and roots bind all nine realms together")

*Image credit: [@norsemythologyclips](https://www.instagram.com/norsemythologyclips/) — go follow them.*

Connective tissue for the Norse Architecture — **`Norse.Hosting`**: the web, worker, and migration service chassis (`Norse.Hosting.Web.Server`/`.Web.Client`/`.App`/`.Worker`/`.Migrations.Service`) and the deployables built on it. It hosts the runtime endpoints that Bifröst composes but never provides itself. In the dependency chain it rides on Midgard and everything below; Himinbjörg and Heimdall ride above it.

## Status

Scaffolded — `Hosting.Web.Server`, `Hosting.Worker`, and `Hosting.Migrations.Service` exist as minimal deployable stubs: placeholder code, passing tests, and container-publishable via `dotnet publish /t:PublishContainer`. Real hosting abstractions land once Asgard and Midgard have shipped their foundations. Each subsequent type surface follows the spec-first discipline: brainstorm → spec → plan in [Glitnir](https://github.com/NorseArchitecture/Glitnir)'s `docs/Yggdrasil/`, greenlit by the human, then code.

## The cosmos

Yggdrasil is one realm of the [Norse Architecture](https://github.com/NorseArchitecture). The whole platform composes at [Bifröst](https://github.com/NorseArchitecture/Bifrost) — clone once, cross the bridge, and every session starts there so decisions get brainstormed across the entire landscape, not in isolation. Every design is tried in [Glitnir](https://github.com/NorseArchitecture/Glitnir), the design court, before code is forged here; this realm's specs and plans will live in the court's [docs/Yggdrasil/](https://github.com/NorseArchitecture/Glitnir/tree/master/docs/Yggdrasil) once they converge.
