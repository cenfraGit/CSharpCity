# CSharpCity

> Note: this project was fully 100% made by claude as an experiment.

Point it at a C# solution and walk the result as a city. Every building is a type, every block a
namespace, every district a project — and everything you can see means exactly one thing about the
code.

```bash
# Analyse once, cache the model
dotnet run --project src/CSharpCity.App -- path/to/Your.sln --analyzers --dump-json city.json --no-render

# Then walk it as often as you like
dotnet run --project src/CSharpCity.App -- --from-json city.json
```

**Repository history is read automatically** whenever the solution sits inside a git working tree —
that is where the cones and bicycles come from. `--no-git` skips it; `--git` forces it.

**`--analyzers` is off by default, and leaving it off switches off whole parts of the city.** A run
without it is not a cleaner city, it is a city missing channels:

| Flag | Without it |
|---|---|
| `--analyzers` | No police, no ambulances, no bins, no posters, no patched render, no letting boards, no idling generators, no ziplines. These all come from Sonar rules; the compiler does not report them. |
| `--no-git` (or no repository) | No cones, no bicycles. The city looks like nowhere anyone is working. |
| `--no-github` (or no `gh`) | No building sites, no road closures, no queues. The city shows the code alone, with no sign of what the team is doing to it. |

What you get either way, because the compiler reports it: fires, broken windows, rubbish, condemned
boards, and everything structural — size, shape, dependencies, civic roles, greenery.

---

## The one rule

**Every visual property is a monotonic function of exactly one metric, and the direction is
intuitive: bigger, darker, more-on-fire is worse.** No visual ever carries two meanings.

The most important consequence: **a building's massing always means its size.** Height is method
count, footprint is state. Nothing — not importance, not decay, not history — is ever allowed to
change a building's shape, because then you could no longer read how big the class is.

---

## Reading it in 30 seconds

From the air:

| What you see | What it means |
|---|---|
| **Brown haze over a district** | That project is complex on average. Thicker = worse. |
| **Fires** | Compile errors, or exceptions being swallowed. |
| **A tall tower** | A class with a lot of methods. |
| **A wide, squat block** | A class with a lot of fields and properties. |
| **Roundabouts** | Circular dependencies. The types on the ring can all reach each other. |
| **Crowds of people on worn paths** | Dependencies. The more people, the more references. |
| **Blocks within a district** | Namespaces. Nested namespaces are nested blocks. |
| **Green parkland** | A test project. |
| **Orange cones everywhere** | That code was committed to **this week**. |

If a district is hazy, on fire and crawling with cranes, you have found the problem without reading
a line of code.

---

## Districts, blocks and namespaces

The city has three levels of address, and they are the three levels of your code:

| Level | Is |
|---|---|
| **District** | A project |
| **Block within a district** | A namespace — and nested namespaces are nested blocks, all the way down |
| **Lot** | A type |

### One city, or a country of towns

`--cities` lays each project out as **its own town**, with open country between them, instead of as
a district of a single city. It says something the packed layout can't: that these are separate
deliverables that happen to ship together, and how far apart they really are.

| What you see | What it means |
|---|---|
| **A town** | A project |
| **Distance between two towns** | Their relative size, and nothing more — the treemap packs them by weight |
| **A rail line across country** | A declared project reference |
| **A line with no train on it** | A reference declared and never used |

**The country between towns is land you could walk across** — low downs, plains and woods. Projects
in one solution are parts of one place, not separate worlds. The hills are kept deliberately low:
anything tall enough to hide the next town turns the map into a maze, and being able to see how the
towns sit relative to one another is the entire point of laying them out this way.

**The map ends at a coastline, not a mountain range.** Every boundary that isn't explained looks
arbitrary, and a wall of peaks around the world was exactly that. Land stops because the sea starts,
which invites no questions. The shoreline is pushed in and out by noise, so it reads as a coast
rather than as a rounded rectangle.

**Roads never leave their own town.** That's deliberate, and it's why the road network is in pieces
here: streets are a town's internal business, and what connects two projects is a project reference —
which the rail already shows. A road between towns would be a second channel saying the same thing
less well. So cars keep to their own town, and the ride-along can only take you within one.

Towns stay flat inside; the ground only shapes itself outside them, because the whole surface-height
stack and the footpath system rest on the city floor being exactly level.

The packed single-city layout is still the default — with its ring of mountains — so the two can be
compared on the same solution.

Blocks are sized by the floor area their contents need, so a namespace's footprint is the bulk of
the code inside it. **Street signs name the block**, so you can navigate by reading them.

One thing to know before you count blocks: **a namespace with no types of its own and a single child
is folded away**, and its sign shows the whole folded chain (`Foo.Bar.Baz`). Without that, a solution
where everything sits under one long shared prefix would spend a street's worth of margin on every
level and leave nothing to build on. So the block structure shows where your namespaces actually
*branch*, not every segment you typed.

### Namespaces set the road tier

This is the one thing streets do mean. A road's width, speed and pavement follow the boundary it
separates:

| Road | Separates | Speed |
|---|---|---|
| **Boulevard** — widest, two lanes each way, broad pavements | Two projects | 50 km/h |
| **Street** — medium, one lane each way | Two namespaces in one project | 30 km/h |
| **Alley** — narrow, minimal pavement | Two lots in the same namespace | 15 km/h |

So the grain of the street plan is the grain of your namespace tree. A district of nothing but alleys
is one flat namespace; a district cut up by boulevards and streets is deeply partitioned.

### Namespaces colour the foot traffic

Footpaths and the people on them are tinted by how far the dependency reaches:

| Path and walkers | Dependency |
|---|---|
| **Trodden earth** path, pale walkers | Within one namespace |
| **Worn stone** path, blue walkers | Across namespaces, same project |
| **Brown** path, orange walkers | Across projects |

Colour only — the path shape is identical. It answers "is this coupling local or does it reach?" at a
glance: **a building surrounded by orange walkers is talking to the whole solution.**

---

## The buildings

### Shape — always about size

| Property | Metric |
|---|---|
| Height | One storey per method |
| Storey height | That method's lines of code |
| Footprint | Fields + properties |
| Plinth (raised base) | Inheritance depth |
| Windows across a facade | That method's parameter count (capped at 8) — six parameters is a wall of glass |

### Type kinds have their own architecture

| Building | Type |
|---|---|
| Ordinary block of storeys | class |
| **Glass pavilion**, low and transparent | interface |
| **Windowless obelisk**, no doors | static class — you cannot enter one |
| **Open scaffolding** on the top storey | abstract class — never finished |
| **Kiosk** with a lit slot per member | enum |
| **Phone booth** | delegate |
| **Warehouse** — no windows, roller shutter, shallow ridge | A type with no methods: it holds state and does nothing with it. DTOs, records, POCOs. |
| Flat parapet cap on the roof | `sealed` |

Two more that describe a building rather than its kind:

| Building | Means |
|---|---|
| **Colonnade and entablature** across the frontage | Untouched for two years — the exact opposite of the traffic cones. Finished, abandoned, or too frightening to edit; the city can't tell which. |
| **Railings** round the plot | An `internal` type: visible from the street, not yours to walk into. A railing rather than a wall, because internal is not secret. |

### Condition — always about quality

| What you see | What it means |
|---|---|
| **Lit windows at night** | Public members. A dark building is all-private. |
| **Soot streaks** | Average complexity ≥ 6 |
| **Smashed windows** | Nullable-reference warnings (`CS86xx`) — more breakage, more panes |
| **Boarded up and unlit, weeds through the paving** | Dead code. One condition, two signs of it. |
| **Fire** | Compile errors, and empty `catch` blocks |
| **Boards nailed diagonally across the entrance** | **Uses** `[Obsolete]` API — condemned by what it depends on, not by its own attribute |
| **Rubbish scattered on the lot** | Unused / unreachable code the compiler flagged |
| **Hazard tape** | `NotImplementedException` |
| **Red light on the roof** | God class — tall enough to be a navigation hazard |
| **Damp and moss on one storey** | No test reaches that method. Needs `--coverage`; see below. |
| **Trees on the lot** | The reward: few findings, low complexity. Never taller than half the building. |
| **A pond, or a sports pitch, in a block** | The whole *namespace* is in good order — not just one lucky class. Pitch needs a 20 m block, pond 34 m. |

### Attachments — one per language feature

Always present:

| Fixture | Means |
|---|---|
| Zigzag **fire escape** | `IDisposable` |
| External **lift shaft** on a storey | that method is `async` — the floor you wait on |
| Roof **loudspeakers** | events, one horn per event (max 5) — this type broadcasts |
| Exterior **pipework** | nesting depth ≥ 4, one run per level past the third |
| **Roof antennae** | interfaces implemented, one each |
| **Doors** at street level | public constructors |

Only with `--analyzers`:

| Fixture | Means |
|---|---|
| **Zipline** off the roof | `goto` — control leaving by something other than the stairs |
| **Fly-posters**, the faded ones older | commented-out code and deprecated API |
| **Patched render** | redundant casts, jumps, initialisers, null-forgiving operators |
| **Letting board** | empty classes, methods and blocks |
| **Idling generator** with exhaust | missing `CancellationToken` overloads — work nobody can stop |
| **Wheelie bins and refuse sacks** | unused locals, fields and dead stores (Sonar's view, alongside the compiler's rubbish above) |

---

## Civic landmarks

Each project awards each role **at most once**, to its most extreme type — which is what keeps them
legible instead of turning every district into a cathedral. Civic status **never changes the
massing**; it only adds dressing around and on top of the real building, plus a plaque stating the
numbers that earned it.

Three things that stop you over-reading them:

- **A building holds one role only.** The list below is priority order, so a type that is both the
  most depended-upon and the most throw-happy becomes a town hall, and the courthouse goes to the
  runner-up.
- **Each role has a minimum bar.** A small project with one unimplemented interface gets no
  cathedral. A missing landmark means "nothing here clears the bar", not "nothing here is notable".
- **Test projects get none.** They are parkland, not a civic centre.

| Landmark | Awarded to |
|---|---|
| **Town hall** — portico, dome, clock | Highest fan-in |
| **Cathedral** — spire ∝ implementors | Interface with most implementors |
| **School** — wings round a courtyard | Abstract class with most derived types |
| **Power station** — cooling towers | Most static mutable state |
| **Depot** — loading bays | Highest cross-project fan-in |
| **Library** — long colonnade | Stateless static class, most public members |
| **Hospital** — red cross, helipad | Most `try`/`catch` |
| **Courthouse** — wide steps, pediment | Most `throw` sites |
| **Factory** — sawtooth roof, chimneys | Most methods returning constructed objects |

---

## History — what the repository remembers

Automatic inside a git repository. The window is **7 days**, so this is *now*, not *this quarter*.

| What you see | What it means |
|---|---|
| **Orange traffic cones** on a lot | Commits this week — one cone per commit. Today's disruption. |
| **Tower crane** beside a building | Outstanding TODO / HACK / FIXME comments. Taller = more. |
| **Bicycles racked at the frontage** | One per distinct author, all-time. How many people work here. |

The pairing is the point. Grime already means complexity, so **a filthy building surrounded by cones
is a churn-times-complexity hotspot** — the code you change constantly *and* that is hard to change
safely. It falls out of two independent channels rather than being invented as a third.

Cones are deliberately neutral. Churn is not a defect, and code that nobody dares touch is a worse
problem than code that changes often.

---

## Coverage — what the tests actually reach

A floor is a method and coverage is per method, so this is the one channel whose grain matches its
metric exactly. Produce a report and point at it:

```bash
dotnet test --collect:"XPlat Code Coverage"
dotnet run --project src/CSharpCity.App -- Your.sln --coverage path/to/coverage.cobertura.xml
```

Storeys whose method is reached by no test go **damp**: green moss rising from the floor slab. Its
nearest neighbour is soot (complexity), which is dark and streaks *downward* from the roof — hue and
direction are what keep two kinds of neglect apart on the same wall.

**Unmeasured is not the same as untested.** Without a report nothing is marked at all, and a method
the report never mentions stays unmarked even when the rest of its file is measured. A method with
no measurable statements — abstract, extern, an auto-property — is uncoverable rather than uncovered,
and is also left alone. Only "measured, and no test went in" grows moss.

---

## Works — what the team is doing right now

Automatic inside a GitHub repository when the [`gh` CLI](https://cli.github.com/) is installed and
signed in. `--no-github` skips it.

This is the only part of the city that is **not** derived from your source, and it is kept strictly
apart from everything else: it dresses buildings that already exist and **never moves one**. A pull
request that deletes a class raises hoarding around the building rather than removing it — the
building is still there on the main branch, which is the honest picture.

### Open pull requests are building sites

`gh` reports the files each pull request touches, so works appear on the **actual buildings the
change affects**, not as a number floating over the city.

| What you see | What it means |
|---|---|
| **Hoarding** round a lot | An open pull request touches this type |
| **Scaffolding**, higher for a bigger diff | How much of the file the change rewrites |
| **Hoarding but no scaffolding** | A draft — fenced off, nobody working |
| **Floodlight** on the scaffold | Touched within the last month |
| **Weathered hoarding, no light** | Nothing has happened for a month |
| **Green ribbon** across the frontage | Approved, waiting to merge |
| **Red notice board** | Changes requested |
| **Amber notice board** | A required check is failing |
| **Hazard tape** all round | The pull request deletes this file |
| **Blue survey drawing** standing on empty ground | A file the pull request *adds* — no building exists yet, so this is where it would go |
| **Road closed**, barriers across the carriageway | The pull request conflicts with its base branch |

The closure is deliberately the one place open-source state touches the roads, and it is an
**incident** in the same sense the fires and ambulances are: rare, capped at six, and specific. It
does not make traffic mean anything in general.

### Issues are the queue outside the civic buildings

Issues carry no file references, so putting one on a building would be a lie. They become what a
backlog actually is — people waiting on the council:

| What you see | What it means |
|---|---|
| **Queue at the hospital** | Open issues labelled as defects |
| **Queue at the town hall** | Everything else: requests, proposals, unlabelled |
| **Standing** | Opened within the last month |
| **Sitting down** | Open more than a month |
| **Tents** | Open more than a year — a backlog nobody is going to clear |

If the solution is too small to have earned a town hall or a hospital, the queues form in the main
square instead. They are an aggregate and they are shown as one.

### Browsing and refreshing

**G** opens the works browser — every open pull request, its state, and the files it touches. Click
one to **isolate** it, so the city shows that change alone; click it again for all of them.
**F12** re-asks the remote.

A refresh rebuilds only the works and the queues. The city underneath is derived from source and
does not move, so nothing you are looking at shifts when somebody opens a pull request.

---

## The ground: dependencies, streets and traffic

**Dependencies are people, roads are scenery.** This is the rule that took three attempts to get
right: drawing dependencies as roads made the graph fight the city, and routing them through the
street grid destroyed the reading — once a route bends round three blocks you cannot see which two
buildings it connects.

| What you see | What it means |
|---|---|
| **Worn footpaths** between buildings, dead straight | A dependency. Path width ∝ reference count. |
| **People walking them** | Density is the reference count. A crowd is heavy coupling. |
| **Path and walker colour** | How far the dependency reaches — see *Namespaces colour the foot traffic* above. |
| **Road width** | The boundary it separates: boulevard = project, street = namespace, alley = neither. |
| **Roundabout with hazard stripes** | A circular dependency. Every type on that ring can reach the others. |
| **Rail line between districts** | A declared project reference. |
| **Trains running on it** | Actual cross-project type usage. |
| **Rusted rail, no trains** | A declared reference nothing uses — *possibly deletable*. Reflection and DI can hide real usage, so check before cutting. |
| **Airport** | External NuGet packages. Apron size ∝ distinct package count. |

A road's **tier** means something (which boundary it separates); everything else about the traffic is
**deliberately meaningless**. Cars, traffic lights, give-way signs, pavements, car parks, highway
decks and street furniture exist so the place reads as a city and so you have something to walk
along. Do not look for meaning in them — there is none, on purpose. Where a particular car is going
says nothing about your code.

Cars are a live simulation: they spawn at a building, pathfind to somewhere else, queue at red
lights, give way, and stop existing when they arrive.

---

## Emergencies — rare on purpose

Drama is capped so it stays meaningful. If everything is an emergency, nothing is.

| What you see | What it means |
|---|---|
| **Police, searchlight beams** | Security findings — weak crypto, ReDoS, hardcoded credentials |
| **Fire engines, water jets** | Swallowed exceptions (empty `catch`) |
| **Ambulances** | Resource leaks — `IDisposable` opened and never closed |
| **Helicopter circling** | The single worst building in the city |

**Read these as a shortlist, not a census.** Each response type is capped at **6 city-wide**, ranked
worst-first. No ambulance on a building does not mean it has no leaks — only that six others were
worse. The conditions layer is where you read totals; this layer only answers *where do I go first*.

Note the split on fires: the *building* burns for compile errors as well as swallowed exceptions,
but only swallowed exceptions summon an engine.

---

## Controls

| Key | Does |
|---|---|
| **WASD**, mouse | Walk and look. **Shift** to sprint — roughly five times walking pace, for crossing a district. |
| **F** | Fly. **WASD** stays level whichever way you look, so height is **Space** / **Ctrl** alone. **Shift** speeds up all of it. |
| **R** | Drive somewhere — opens a searchable list of every building. Flying, it flies you instead. |
| **C** | Fly to the next incident — fires, crime scenes, cycles, sole ownership |
| **B** | Worst buildings, ranked. Press **1**–**0** to fly to one. |
| **Tab** | Day / night |
| **Scroll** | Field of view |
| **=** | Fast-forward traffic |
| **F1** | Key legend |
| **F8** | Inspection card under the crosshair |
| **F11** | Fullscreen |
| **L / T / M** | Labels / traffic / minimap |
| **G** | The works browser — open pull requests; click one to isolate it |
| **F12** | Re-ask the remote for pull requests and issues |
| **F2**–**F7**, **F9**, **F10** | Toggle one layer: smog, rail, roundabouts, footpaths, highways, air, people, sidewalks — in that order |
| **O** / **P** | Toggle works / backlog queues |
| **Esc** | Release the mouse |
| **Ctrl+Q** | Quit |

Any movement key cancels a tour or a ride, so you are never stuck in a cutscene.

---

## What the console tells you

Things the city cannot show well. These go to **stderr** as `note:` lines, so keep it on the terminal
or capture it with `2>notes.txt` — piping stdout alone loses all of it:

- **The architectural seam.** The split of your projects that the fewest references cross, how much
  leaks across it, and over how many project pairs. A clean layering shows a couple of heavy pairs
  (everything goes through one interface); a tangle shows a long tail of light ones. Same leakage,
  completely different design.
- **Unused project references** — declared but carrying no observed type usage.
- **Road network connectivity**, circular dependency count, civic landmark tally, and any buildings
  that overran a lot too small to hold them.

---

## Reading it honestly

A few limits worth knowing before you draw conclusions:

- **History is per file, not per type.** Several types in one file report identical history, and a
  partial type only records the file it was first seen in.
- **Dependency edges come from static analysis.** Reflection, DI containers and dynamic dispatch are
  invisible to it, which is why "rusted rail" is a question rather than an answer.
- **Some channels are gameable.** The cheapest way to remove a crane is to delete the TODO comment,
  which improves nothing. Treat the city as a map of where to look, not as a score to optimise.
- **Absence is ambiguous.** A missing feature can mean the code is clean, that the metric didn't
  clear a cap or a minimum bar, or that you ran without `--analyzers`, or that the solution isn't in
  a repository. Check which run you are looking at before concluding a district is healthy.
