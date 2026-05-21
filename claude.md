
### Regions
Every class must define the following regions (only include regions that apply):
```csharp
public class FooService
{
    #region Fields
    #endregion

    #region Properties
    #endregion

    #region Unity Callbacks
    #endregion

    #region Public Methods
    #endregion

    #region Private/Protected Methods
    #endregion
}
```

### Comments
- All comments in English.
- Every public method, property, and non-obvious field must have a comment.
- No commented-out dead code committed to the branch.

### Simplicity
- No over-engineering. Solve the current problem, not hypothetical future ones.
- No premature abstractions — an interface earns its place when there are two implementations.
- If a method is longer than ~30 lines, it is doing too much — split it.
- Prefer clear, flat code over clever, nested code.

### Testability
- Every phase must be playable and testable before moving to the next.
- New services must work in isolation — they should not require the full game to be running to validate.
- Create a minimal test scene per phase when needed to validate the new system before wiring it into the main game scene.
- **Every new service MUST ship with its own unit tests.** A service without tests is not considered complete.
  - Tests live under `Assets/Scripts/Tests/EditMode/` (or `PlayMode/` when Unity runtime is required).
  - Inject dependencies (clock, storage, notifications, etc.) so the service can be exercised without the full game running.
  - At minimum cover: happy path, edge cases, and any bug previously fixed in that service