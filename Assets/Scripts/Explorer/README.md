# Third-person exploration

The reusable assets are `Assets/Resources/Prefabs/ExplorerPlayer.prefab` and `NpcCapsule.prefab`. The player is a capsule with a CharacterController; NPCs are solid capsules with a configurable `NpcActor`.

At the initial vehicle spawn, an example NPC is placed nearby. Stop the vehicle and press **F** to exit. Use **WASD** to move relative to the camera, hold **Shift** to run, **Space** to jump, and move the **mouse** to orbit, just like driving (gamepad: right stick). Use the mouse wheel to zoom the walking camera in or out. The walking camera starts at a 50-degree downward angle and stays within 38–65 degrees, retaining an overhead view while allowing a full horizontal orbit. Pausing or opening the console releases the cursor. Approach the vehicle and press **F** to drive again. Console and pause input do not move the character. Terrain and grass streaming follow the controlled character when on foot.

Interaction follows Cementery's `WorldObject` / Inspector-event pattern (`CementeryRef/Assets/Scripts/WorldObject.cs`), using proximity instead of its camera ray. The closest visible enabled target is selected within 6 metres. A **?** above the player's head means something is nearby; **!** means it is within the 2.5-metre interaction range. Solid walls block targeting. F invokes `onInteract` once; NPCs also face the player and advance through their configured dialogue lines. Carrying, inventory, combat and pathfinding are outside this initial NPC system.

Change names, dialogue and interaction events on the NPC prefab or its scene instances. Add `WorldInteractable` to other objects to use the same targeting and head hints. The menu **Voyage / Generate Explorer Prefabs** creates missing assets and preserves existing prefab edits.

Movement borrows Cementery's short jump buffer and coyote time, while using a CharacterController and the existing third-person collision-aware camera. The head hints are original simple punctuation indicators inspired by the requested Yomawari presentation.
