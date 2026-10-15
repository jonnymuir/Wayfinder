# Glossary

Wayfinder's model is the
[Nielsen Norman Group service blueprint](https://www.nngroup.com/articles/service-blueprints-definition/)
(Sarah Gibbons, 2017), so the first column uses NN/g's terms. The plain-language column is the
wording the Wayfinder.Umbraco README and walkthroughs use for people who don't read JSON.
[Wayfinder.Umbraco](https://github.com/jonnymuir/Wayfinder.Umbraco) and
[Umbraco Prism](https://github.com/jonnymuir/Umbraco.Prism) use the same terms.

## The service blueprint model

| NN/g term | Wayfinder term | Plain language | What it is |
|---|---|---|---|
| Service blueprint | `ServiceBlueprint` | Service, blueprint | The whole service: its queues, stages, gateways and calculations, as one JSON document. |
| Lane (customer actions, frontstage, backstage) | Queue (`queues`) | Lane, queue, team | A lane of work owned by one actor: the citizen, a caseworker team, a system. The worklist renders a queue. |
| Support processes | Support system | Another system, integration | An external system a stage calls and waits on. See [support systems](./guides/support-systems.md). |
| Line of interaction, visibility, internal interaction | (implicit) | | The boundary between two lanes. A route that crosses lanes crosses one of these lines. |
| Step in the journey | Stage (`stages`) | Step, page | One screen or task. Every stage belongs to exactly one queue, which is what puts it in a lane. |
| Arrow between steps | Gateway (`gateways`) | Decision point | A routing node between stages. A stage's routes always target a gateway, never another stage. |
| | Route | Path, branch | One way out of a stage or gateway, chosen by its `trigger` and optionally its `showWhen`. |
| | Split / Join gateway | | A Split fans out (every matching route fires). A Join waits until the paths it needs have arrived. |
| | Component | Question, field, content | A GOV.UK Design System element a stage renders: an input, a panel, a summary list, a chart. |
| | `calculations` | Rules, maths | The declarative, side-effect-free expressions behind a blueprint's numbers and conditions. See [the calculation language](./guides/calculation-language.md). |
| | `showWhen` | Only show if | An expression that decides whether a component or route is shown. |

## Running a service

| Wayfinder term | Plain language | What it is |
|---|---|---|
| Service request, instance | Application, case | One person's run through a blueprint. Identified by an `instanceId`. |
| `GetCurrent` / `Advance` | Show the step / submit it | The two engine calls a host needs: render the current stage, then submit it. |
| Response state (`render`, `defer`, `complete`, `error`) | | What the host should do next with an envelope: show a stage, wait and poll, show the end, or report a problem. |
| `ActorProfile` | Access | Which queues the signed-in person can see, start and act on. The host resolves it; the engine enforces it. |
| Pickup / putback | Take / release | Claiming an item on a shared queue so only you work it, and handing it back. See [work allocation](./guides/work-allocation.md). |
| Team tray (`team-tray`) | Team queue | A queue only one team works; picked-up items stay with their holder across waits. See [team assignment](./guides/team-assignment.md). |
| `requestPolicy` (`single`, `multiple`, `prompt`) | One or many | Whether a person resumes their one active request, always starts a new one, or is asked. |
| Service inputs (`source: "service"`) | Data from elsewhere | Values the host supplies to a blueprint at run time, such as the signed-in user or a member record. |
